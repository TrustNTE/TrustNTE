using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using TrustNTE.Helpers;
using TrustNTE.Models.Toolkit;
using TrustNTE.Services.Interfaces;
using TrustNTE.Services.Toolkit;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace TrustNTE.ViewModels
{
    public partial class ScreenshotEditorViewModel : ObservableObject
    {
        private const int MaxImageSide = 4000;

        private readonly IFileDialogService _fileDialogs;
        private readonly IClipboardService _clipboard;
        private readonly ILogService _log;
        private readonly IConfigService _configService;
        private readonly MagnifierService _magnifier;
        private readonly DevToolsConfig _cfg;

        private Mat? _editMat;     // BGR 3通道
        private Mat? _mask;        // 8UC1 透明掩膜(null=全不透明);255=保留 0=透明
        private Mat? _undoMat;
        private Mat? _undoMask;
        private DateTime _lastMag = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer? _colorsDebounce;

        [ObservableProperty] private BitmapSource? _editorImage;
        [ObservableProperty] private EditorTool _currentTool = EditorTool.Move;
        [ObservableProperty] private double _scale = 1.0;
        [ObservableProperty] private string _rgbInput = "#000000";
        [ObservableProperty] private Brush _rgbSwatch = Brushes.Black;
        [ObservableProperty] private int _fillTolerance = 10;
        [ObservableProperty] private int _cropTolerance = 10;
        [ObservableProperty] private int _binThreshold = 128;
        [ObservableProperty] private string _rectResult = "";
        [ObservableProperty] private string _statusText = "";
        [ObservableProperty] private bool _canUndo;
        [ObservableProperty] private EditorColorItem? _selectedColor;

        public ObservableCollection<EditorColorItem> Colors { get; } = new();

        public event Action? CloseRequested;
        /// <summary>选区视觉状态变化(窗口重绘虚线/反选遮罩)。</summary>
        public event Action? SelectionVisualChanged;

        // —— 选区状态(图像像素坐标)——
        public OpenCvSharp.Rect? SelRectPx { get; private set; }
        public bool SelIsCircle { get; private set; }
        public bool SelInverted { get; private set; }
        public bool HasSelection => SelRectPx != null;

        public ScreenshotEditorViewModel(IFileDialogService fileDialogs, IClipboardService clipboard,
            ILogService log, IConfigService configService, MagnifierService magnifier)
        {
            _fileDialogs = fileDialogs;
            _clipboard = clipboard;
            _log = log;
            _configService = configService;
            _magnifier = magnifier;

            _cfg = _configService.Load().DevTools;
            DevToolsLegacyMigrator.TryMigrate(_cfg);

            FillTolerance = Math.Clamp(_cfg.FillTolerance, 0, 255);
            CropTolerance = Math.Clamp(_cfg.CropTolerance, 0, 255);
            BinThreshold = Math.Clamp(_cfg.BinThreshold, 0, 255);
            foreach (var hex in _cfg.EditorColors.Take(50))
                Colors.Add(new EditorColorItem(hex));
        }

        partial void OnFillToleranceChanged(int value) => _cfg.FillTolerance = value;
        partial void OnCropToleranceChanged(int value) => _cfg.CropTolerance = value;
        partial void OnBinThresholdChanged(int value) => _cfg.BinThreshold = value;

        // ============ 工具模式(setter 只响应 true) ============
        public bool IsMoveTool { get => CurrentTool == EditorTool.Move; set { if (value) SetTool(EditorTool.Move); } }
        public bool IsRectTool { get => CurrentTool == EditorTool.Rect; set { if (value) SetTool(EditorTool.Rect); } }
        public bool IsCircleTool { get => CurrentTool == EditorTool.Circle; set { if (value) SetTool(EditorTool.Circle); } }
        public bool IsFillTool { get => CurrentTool == EditorTool.Fill; set { if (value) SetTool(EditorTool.Fill); } }
        public bool IsPickTool { get => CurrentTool == EditorTool.Pick; set { if (value) SetTool(EditorTool.Pick); } }

        private void SetTool(EditorTool tool)
        {
            if (CurrentTool == tool) return;
            CurrentTool = tool;
            OnPropertyChanged(nameof(IsMoveTool));
            OnPropertyChanged(nameof(IsRectTool));
            OnPropertyChanged(nameof(IsCircleTool));
            OnPropertyChanged(nameof(IsFillTool));
            OnPropertyChanged(nameof(IsPickTool));
        }

        // ============ 载入 ============
        public void LoadFromFrame(Mat snapshotBgr)
        {
            ClearUndo();
            _editMat?.Dispose();
            _editMat = snapshotBgr;
            _mask?.Dispose(); _mask = null;
            ClearSelection();
            Scale = 1.0;
            RectResult = "";
            RefreshImage();
            StatusText = $"已载入 {_editMat.Width}×{_editMat.Height}";
        }

        public Mat? GetEditMat() => _editMat;

        // ============ 渲染(含透明通道) ============
        private void RefreshImage()
        {
            if (_editMat == null || _editMat.Empty()) { EditorImage = null; return; }
            if (_mask == null)
            {
                EditorImage = ImageHelper.MatToBitmapSource(_editMat);
            }
            else
            {
                using var bgra = ComposeBgraWithAlpha();
                // 修复:bufferSize 参数是 int,加 (int) 强转
                var bs = BitmapSource.Create(bgra.Width, bgra.Height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null,
                    bgra.Data, (int)((long)bgra.Width * bgra.Height * 4), bgra.Width * 4);
                bs.Freeze();
                EditorImage = bs;
            }
        }

        private Mat ComposeBgraWithAlpha()
        {
            using var tmp = new Mat();
            Cv2.CvtColor(_editMat!, tmp, ColorConversionCodes.BGR2BGRA);
            var chans = Cv2.Split(tmp);
            chans[3].Dispose();
            chans[3] = _mask!.Clone();
            var dst = new Mat();
            Cv2.Merge(chans, dst);
            foreach (var c in chans) c.Dispose();
            return dst;
        }

        /// <summary>编辑器悬停:读数 + 悬浮放大镜。</summary>
        public void OnImageMouseMove(int px, int py)
        {
            if (_editMat == null) return;
            px = Math.Clamp(px, 0, _editMat.Cols - 1);
            py = Math.Clamp(py, 0, _editMat.Rows - 1);
            var v = _editMat.At<Vec3b>(py, px);
            string info = $"({px},{py})  #{v.Item2:X2}{v.Item1:X2}{v.Item0:X2}";

            if ((DateTime.Now - _lastMag).TotalMilliseconds >= 33)
            {
                _lastMag = DateTime.Now;
                using var mag = new Mat();
                Cv2.GetRectSubPix(_editMat, new OpenCvSharp.Size(25, 25), new Point2f(px + 0.5f, py + 0.5f), mag);
                using var big = new Mat();
                Cv2.Resize(mag, big, new OpenCvSharp.Size(170, 170), 0, 0, InterpolationFlags.Nearest);
                _magnifier.Update(ImageHelper.MatToBitmapSource(big), info);
            }
        }

        // ============ 撤销(含掩膜) ============
        private void PushUndo()
        {
            if (_editMat == null) return;
            _undoMat?.Dispose(); _undoMat = _editMat.Clone();
            _undoMask?.Dispose(); _undoMask = _mask?.Clone();
            CanUndo = true;
        }

        private void ClearUndo() { _undoMat?.Dispose(); _undoMat = null; _undoMask?.Dispose(); _undoMask = null; CanUndo = false; }

        [RelayCommand]
        private void Undo()
        {
            if (_undoMat == null || _editMat == null) return;
            (_editMat, _undoMat) = (_undoMat, _editMat);
            (_mask, _undoMask) = (_undoMask, _mask);
            ClearUndo();
            RefreshImage();
            StatusText = "已撤销";
        }

        // ============ 选区 ============
        private void FireSel()
        {
            OnPropertyChanged(nameof(HasSelection));
            SelectionVisualChanged?.Invoke();
        }

        private void ClearSelection()
        {
            SelRectPx = null; SelInverted = false;
            FireSel();
        }

        /// <summary>矩形/圆形拖完:选区保留(虚线常驻)。</summary>
        public void CompleteRect(int x, int y, int w, int h, bool isCircle)
        {
            SelRectPx = new OpenCvSharp.Rect(x, y, w, h);
            SelIsCircle = isCircle;
            SelInverted = false;
            RectResult = $"{x},{y},{w},{h}";
            StatusText = $"{(isCircle ? "圆形" : "矩形")}选区 {RectResult} — 可反选/删选区外/填选区外/自动剪裁";
            FireSel();
        }

        [RelayCommand]
        private void InvertSelection()
        {
            if (SelRectPx == null) { StatusText = "先用矩形/圆形工具框选区域"; return; }
            SelInverted = !SelInverted;
            FireSel();
            StatusText = SelInverted ? "已反选:操作作用于选区外" : "已取消反选";
        }

        [RelayCommand]
        private void ClearSel()
        {
            ClearSelection();
            StatusText = "已取消选区";
        }

        /// <summary>构造"保留区"掩膜:选区形状=255;反选后取反。</summary>
        private Mat BuildKeepMask(OpenCvSharp.Size size)
        {
            var keep = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
            if (SelRectPx is { } r)
            {
                if (SelIsCircle)
                {
                    // 修复:用整数版 Point/Size 重载(Point2f/Size2f 重载在你的 OpenCvSharp 版本里不存在)
                    Cv2.Ellipse(keep, new OpenCvSharp.Point(r.X + r.Width / 2, r.Y + r.Height / 2),
                        new OpenCvSharp.Size(r.Width / 2, r.Height / 2), 0, 0, 360, Scalar.All(255), -1);
                }
                else
                {
                    Cv2.Rectangle(keep, r, Scalar.All(255), -1);
                }
                if (SelInverted) Cv2.BitwiseNot(keep, keep);
            }
            return keep;
        }

        private void EnsureMask()
        {
            if (_editMat == null) return;
            if (_mask == null || _mask.Size() != _editMat.Size())
            {
                _mask?.Dispose();
                _mask = new Mat(_editMat.Size(), MatType.CV_8UC1, Scalar.All(255));
            }
        }

        [RelayCommand]
        private void DeleteOutside()
        {
            if (_editMat == null || SelRectPx == null) { StatusText = "先用矩形/圆形工具框选区域"; return; }
            PushUndo();
            EnsureMask();
            using var keep = BuildKeepMask(_editMat.Size());
            Cv2.BitwiseAnd(_mask!, keep, _mask!); // 选区外透明
            RefreshImage();
            StatusText = "选区外已删除(透明),保存 PNG 保留透明";
        }

        [RelayCommand]
        private void FillOutside()
        {
            if (_editMat == null || SelRectPx == null) { StatusText = "先用矩形/圆形工具框选区域"; return; }
            var hex = RgbInput.TrimStart('#');
            if (hex.Length != 6) { StatusText = "RGB 框格式无效(需 #RRGGBB)"; return; }
            byte r = Convert.ToByte(hex[..2], 16), g = Convert.ToByte(hex.Substring(2, 2), 16), b = Convert.ToByte(hex[4..], 16);

            PushUndo();
            EnsureMask();
            using var keep = BuildKeepMask(_editMat.Size());
            using var outside = new Mat();
            Cv2.BitwiseNot(keep, outside);

            using var color = new Mat(_editMat.Size(), _editMat.Type(), new Scalar(b, g, r));
            _editMat.SetTo(color, outside);                       // 选区外涂色
            Cv2.BitwiseOr(_mask!, outside, _mask!);               // 选区外恢复不透明
            RefreshImage();
            StatusText = $"选区外已填充 #{hex.ToUpperInvariant()}(不透明)";
        }

        // ============ 自动剪裁:有选区=裁到选区;无选区=四角同色/非透明包围盒 ============
        [RelayCommand]
        private void AutoCrop()
        {
            if (_editMat == null) return;
            if (HasSelection) { CropToSelection(); return; }

            if (_mask != null)
            {
                // 已有透明区域:按非透明内容包围盒裁(四角同色法对透明图无效)
                var rect = Cv2.BoundingRect(_mask);
                if (rect.Width <= 1 || rect.Height <= 1) { StatusText = "没有可见内容可裁剪"; return; }
                PushUndo();
                CropBoth(rect);
                RefreshImage();
                StatusText = $"已按非透明内容裁剪 → {_editMat!.Width}×{_editMat.Height}";
                return;
            }

            var cropped = ScreenEditorOps.AutoCrop(_editMat, CropTolerance);
            if (cropped == null) { StatusText = $"自动剪裁无效:四角颜色不一致(容差 {CropTolerance})或无前景"; return; }
            PushUndo();
            _editMat.Dispose();
            _editMat = cropped;
            Scale = 1.0;
            RefreshImage();
            StatusText = $"已剪裁 → {_editMat.Width}×{_editMat.Height}";
        }

        private void CropToSelection()
        {
            if (_editMat == null || SelRectPx is not { } r) return;
            bool circle = SelIsCircle;
            r = ClampRect(r);
            PushUndo();
            CropBoth(r);
            if (circle)
            {
                EnsureMask();
                using var keep = new Mat(_editMat!.Size(), MatType.CV_8UC1, Scalar.All(0));
                // 修复:整数版 Point/Size 重载
                Cv2.Ellipse(keep, new OpenCvSharp.Point(r.Width / 2, r.Height / 2),
                    new OpenCvSharp.Size(r.Width / 2, r.Height / 2), 0, 0, 360, Scalar.All(255), -1);
                Cv2.BitwiseAnd(_mask!, keep, _mask!); // 圆外透明
            }
            ClearSelection();
            Scale = 1.0;
            RectResult = $"{r.X},{r.Y},{r.Width},{r.Height}";
            RefreshImage();
            StatusText = $"已裁剪到选区 → {_editMat!.Width}×{_editMat.Height}{(circle ? ",圆外已透明" : "")}";
        }

        private void CropBoth(OpenCvSharp.Rect r)
        {
            var cm = new Mat(_editMat!, r).Clone();
            _editMat!.Dispose(); _editMat = cm;
            if (_mask != null) { var mk = new Mat(_mask, r).Clone(); _mask.Dispose(); _mask = mk; }
        }

        private OpenCvSharp.Rect ClampRect(OpenCvSharp.Rect r)
        {
            int x = Math.Clamp(r.X, 0, Math.Max(0, _editMat!.Width - 1));
            int y = Math.Clamp(r.Y, 0, Math.Max(0, _editMat.Height - 1));
            int w = Math.Clamp(r.Width, 1, _editMat.Width - x);
            int h = Math.Clamp(r.Height, 1, _editMat.Height - y);
            return new OpenCvSharp.Rect(x, y, w, h);
        }

        // ============ 模式工具 ============
        public void PickAt(int px, int py)
        {
            if (_editMat == null) return;
            var (r, g, b) = ScreenEditorOps.PickColor(_editMat, px, py);
            string hex = $"{r:X2}{g:X2}{b:X2}";
            RgbInput = "#" + hex;
            RgbSwatch = new SolidColorBrush(Color.FromRgb(r, g, b));
            var item = new EditorColorItem(hex);
            Colors.Add(item);
            SelectedColor = item;
            StatusText = $"吸管:({px},{py}) → #{hex}";
            SaveColorsSoon();
        }

        public void FillAt(int px, int py)
        {
            if (_editMat == null) return;
            var hex = RgbInput.TrimStart('#');
            if (hex.Length != 6) { StatusText = "RGB 框格式无效(需 #RRGGBB)"; return; }
            byte r = Convert.ToByte(hex[..2], 16), g = Convert.ToByte(hex.Substring(2, 2), 16), b = Convert.ToByte(hex[4..], 16);

            PushUndo();
            if (!ScreenEditorOps.FloodFill(_editMat, px, py, r, g, b, FillTolerance))
            {
                ClearUndo();
                StatusText = "填充无效:点击处颜色与填充色相同";
                return;
            }
            RefreshImage();
            StatusText = $"已填充 ({px},{py}) 容差 {FillTolerance}";
        }

        // ============ 其他命令 ============
        [RelayCommand] private void ZoomIn() => Scale = Math.Min(8.0, Scale * 1.2);
        [RelayCommand] private void ZoomOut() => Scale = Math.Max(0.5, Scale / 1.2);

        [RelayCommand]
        private void Binarize()
        {
            if (_editMat == null) return;
            PushUndo();
            var result = ScreenEditorOps.Binarize(_editMat, BinThreshold);
            _editMat.Dispose();
            _editMat = result;
            RefreshImage(); // 掩膜不动 → 透明区域保持透明
            StatusText = $"已二值化(灰度阈值 {BinThreshold})";
        }

        [RelayCommand]
        private void RemoveColor()
        {
            if (SelectedColor == null) return;
            Colors.Remove(SelectedColor);
            SaveColorsSoon();
        }

        [RelayCommand]
        private void LoadSelectedColor()
        { if (SelectedColor != null) RgbInput = "#" + SelectedColor.Hex; }

        [RelayCommand]
        private void CopyRect()
        {
            if (string.IsNullOrEmpty(RectResult)) { StatusText = "还没有选框结果"; return; }
            if (_clipboard.SetText(RectResult)) StatusText = $"已复制: {RectResult}";
            else StatusText = "复制失败:剪贴板被占用";
        }

        [RelayCommand]
        private void LoadImageFromFile()
        {
            // TODO 未验证:IFileDialogService 打开文件方法名未见接口原文。
            // 报 CS1061 → 打开 Services\Interfaces\IUIServices.cs 搜 "Open" 改成真实方法名。
            var path = _fileDialogs.PickOpenFile("图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp", "");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var mat = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
                if (mat.Empty()) { StatusText = "图片解码失败"; return; }
                int maxSide = Math.Max(mat.Cols, mat.Rows);
                if (maxSide > MaxImageSide)
                {
                    double k = (double)MaxImageSide / maxSide;
                    Cv2.Resize(mat, mat, new OpenCvSharp.Size((int)(mat.Cols * k), (int)(mat.Rows * k)));
                }
                if (mat.Channels() == 4) Cv2.CvtColor(mat, mat, ColorConversionCodes.BGRA2BGR);
                LoadFromFrame(mat);
            }
            catch (Exception ex) { StatusText = $"载入失败: {ex.Message}"; }
        }

        [RelayCommand]
        private void SaveReturn()
        {
            if (_editMat == null) { CloseRequested?.Invoke(); return; }
            string path = Path.Combine(DevToolsWindowViewModel.ImageDir,
                                       $"Edit_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            Mat? composed = null;
            Mat toSave = _editMat;
            if (_mask != null) { composed = ComposeBgraWithAlpha(); toSave = composed; }
            if (Cv2.ImWrite(path, toSave)) _log.LogInfo($"[截图器] 编辑图已保存: {path}");
            else StatusText = "保存失败";
            composed?.Dispose();
            CloseRequested?.Invoke();
        }

        [RelayCommand]
        private void Discard() => CloseRequested?.Invoke();

        // ============ RGB 框 / 颜色表持久化 ============
        partial void OnRgbInputChanged(string value)
        {
            var hex = value.TrimStart('#');
            if (hex.Length == 6)
            {
                try { RgbSwatch = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + hex)); } catch { }
            }
        }

        private void SaveColorsSoon()
        {
            if (_colorsDebounce == null)
            {
                _colorsDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _colorsDebounce.Tick += (_, _) => { _colorsDebounce.Stop(); SaveColorsNow(); };
            }
            _colorsDebounce.Stop(); _colorsDebounce.Start();
        }

        private void SaveColorsNow() =>
            _configService.Update(c => { c.DevTools.EditorColors = Colors.Select(x => x.Hex).ToList(); });
    }

    public enum EditorTool { Move, Rect, Circle, Fill, Pick }

    public partial class EditorColorItem : ObservableObject
    {
        public string Hex { get; }
        [ObservableProperty] private Brush _swatch;

        public EditorColorItem(string hex)
        {
            Hex = hex;
            try { _swatch = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + hex)); }
            catch { _swatch = Brushes.Black; }
        }
    }
}
