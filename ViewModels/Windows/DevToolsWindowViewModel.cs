using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using System.IO;
using TrustNTE.Helpers;
using TrustNTE.Models.Toolkit;
using TrustNTE.Services;
using TrustNTE.Services.Interfaces;
using TrustNTE.Services.Toolkit;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace TrustNTE.ViewModels
{
    public partial class DevToolsWindowViewModel : ObservableObject
    {
        private const int PickSlots = 10;
        private const int MagSrcPx = 25;
        private const int MagDispPx = 170;
        private const int MinIntervalMs = 50;
        private const int MaxIntervalMs = 2000;
        private const int DefaultIntervalMs = 500;

        private readonly IGameService _game;
        private readonly IClipboardService _clipboard;
        private readonly IFileDialogService _fileDialogs;
        private readonly ILogService _log;
        private readonly IConfigService _configService;
        private readonly NteRenderContextManager _render;
        private readonly MagnifierService _magnifier;
        private readonly ImageEditorWindowService _editorWindows;
        private readonly DevToolsConfig _cfg;

        private readonly IShellService _shell;

        private CancellationTokenSource? _liveCts;
        private WriteableBitmap? _wb;
        private Mat? _frame;
        private Mat? _regionBase;  
        private bool _freezeRestore;
        private DateTime _lastMagUpdate = DateTime.MinValue;
        private readonly DispatcherTimer _saveDebounce;

        [ObservableProperty] private BitmapSource? _frameImage;
        [ObservableProperty] private bool _isFrozen;
        [ObservableProperty] private bool _isLive = true;
        [ObservableProperty] private int _previewIntervalMs = DefaultIntervalMs;
        [ObservableProperty] private string _statusText = "等待画面 — 点击【抓取画面】或保持实时预览";
        [ObservableProperty] private string _damoText = "";
        [ObservableProperty] private PickPointItem? _selectedPick;
        [ObservableProperty] private bool _isColorPickerTab = true;
        [ObservableProperty] private bool _isEditorTab;
        [ObservableProperty] private bool _magnifierOn = true;

        public ObservableCollection<PickPointItem> Picks { get; } = new();

        public DevToolsWindowViewModel(
          IGameService game, 
          IClipboardService clipboard, 
          IFileDialogService fileDialogs,
           ILogService log, 
           IConfigService configService, 
           NteRenderContextManager render,
              MagnifierService magnifier, 
              ImageEditorWindowService editorWindows,
                 
              IShellService shell)
        {
            _game = game;
            _clipboard = clipboard;
            _fileDialogs = fileDialogs;
            _log = log;
            _configService = configService;
            _render = render;
            _magnifier = magnifier;
            _editorWindows = editorWindows;
            _shell = shell;
            _cfg = _configService.Load().DevTools;
            DevToolsLegacyMigrator.TryMigrate(_cfg);

            for (int i = 1; i <= PickSlots; i++) Picks.Add(new PickPointItem(i));
            LoadPicksFromConfig();
            foreach (var p in Picks)
                p.PropertyChanged += (_, _) => { RefreshDamo(); SaveSoon(); };

            _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SavePicksNow(); };

            PreviewIntervalMs = Math.Clamp(_cfg.PreviewIntervalMs, MinIntervalMs, MaxIntervalMs);
            _magnifier.Enabled = true;
        }

        /// <summary>截图默认目录: UserData\DeveloperTools\DeveloperTools_Image(不存在自动创建)</summary>
        public static string ImageDir
        {
            get
            {
                string target = Path.Combine(FindUserDataRoot(), "DeveloperTools", "DeveloperTools_Image");
                Directory.CreateDirectory(target);   // 幂等:已存在不会报错
                return target;
            }
        }

        /// <summary>从 exe 目录向上最多 6 级找已有的 UserData 根(兼容 bin\Debug 与发布目录)。</summary>
        private static string FindUserDataRoot()
        {
            string? cur = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && cur != null; i++)
            {
                string candidate = Path.Combine(cur, "UserData");
                if (Directory.Exists(candidate)) return candidate;
                cur = Path.GetDirectoryName(cur);
            }
            return Path.Combine(AppContext.BaseDirectory, "UserData"); // 没找到:落回 exe 旁,首次保存时自动建
        }


        partial void OnMagnifierOnChanged(bool value) => _magnifier.Enabled = value;

        partial void OnIsColorPickerTabChanged(bool value) { if (value) IsEditorTab = false; }
        partial void OnIsEditorTabChanged(bool value) { if (value) IsColorPickerTab = false; }

        partial void OnIsLiveChanged(bool value)
        { if (value && !IsFrozen) StartLive(); else StopLive(); }

        partial void OnIsFrozenChanged(bool value)
        { if (value) StopLive(); else if (IsLive) StartLive(); }

        partial void OnPreviewIntervalMsChanged(int value)
        {
            _cfg.PreviewIntervalMs = value;
            if (IsLive && !IsFrozen) StartLive();
        }

        private void StartLive()
        {
            StopLive();
            _liveCts = new CancellationTokenSource();
            var token = _liveCts.Token;
            int interval = Math.Clamp(PreviewIntervalMs, MinIntervalMs, MaxIntervalMs);

            _ = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try { GrabOneFrame(live: true); } catch { }
                    int delay = interval - (int)sw.ElapsedMilliseconds;
                    if (delay > 1)
                    {
                        if (token.WaitHandle.WaitOne(delay)) break;
                    }
                }
            }, token);

        }

        private void StopLive() { _liveCts?.Cancel(); _liveCts = null; }

        public void OnWindowActivated() { if (IsLive && !IsFrozen) StartLive(); }
        public void OnWindowClosed()
        {
            StopLive(); SavePicksNow(); _magnifier.Hide();
            _regionBase?.Dispose(); _regionBase = null;
        }

        // ============ 命令 ============
        [RelayCommand]
        private void GrabOnce()
        {
            if (!EngineGuard.EnsureReady()) return;   // 引擎没开:弹窗,不空跑
            Task.Run(() => GrabOneFrame());
        }

        [RelayCommand]
        private void ToggleFreeze() => IsFrozen = !IsFrozen;

        [RelayCommand]
        private void ClearPicks()
        {
            foreach (var p in Picks) p.Reset();
            RefreshDamo(); SaveSoon();
            StatusText = "取色表已清空";
        }

        [RelayCommand]
        private void CopyDamo()
        {
            if (string.IsNullOrEmpty(DamoText)) { StatusText = "格式为空:先在画面上取色并勾选启用"; return; }
            CopyText(DamoText);
            StatusText = "已复制多点找色格式";
            _log.LogInfo($"[工具箱] 已复制格式({DamoText.Split('|').Length} 点,1080P 逻辑坐标)");
        }

        public void CopyText(string text) => _clipboard.SetText(text);

        [RelayCommand]
        private void SaveImage()
        {
            if (_frame == null || _frame.Empty()) { StatusText = "还没有画面可保存"; return; }
            string path = Path.Combine(ImageDir, $"DevTools_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            if (Cv2.ImWrite(path, _frame))
            {
                CopyText(path); 
                StatusText = $"已保存: {path}";
                _log.LogInfo($"[工具箱] 画面已保存: {path}");
            }
            else StatusText = "保存失败";
        }
        [RelayCommand]
        private void OpenImageDir() => _shell.OpenFolder(ImageDir);

        [RelayCommand]
        private void CaptureToEditor()
        {
            Task.Run(() =>
            {
                var m = GrabOneFrame(toEditor: true);
                if (m == null) return;
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    _editorWindows.Show(m);
                    StatusText = "已截图 → 编辑器";
                });
            });
        }

        [RelayCommand]
        private void OpenEditor()
        {
            if (_frame != null && !_frame.Empty()) { _editorWindows.Show(_frame.Clone()); return; }
            CaptureToEditorCommand.Execute(null);
        }

        // ============ 框选截图(按下冻结快照 → 松手裁快照,所见即所得) ============
        /// <summary>截图器页按下鼠标开始拖框:冻结画面并快照当前帧。</summary>
        public void BeginRegionCapture()
        {
            if (_frame == null || _frame.Empty()) { StatusText = "还没有画面:先抓取或开启实时预览"; return; }
            _regionBase?.Dispose();
            _regionBase = _frame.Clone();
            _freezeRestore = IsFrozen;
            IsFrozen = true; // 拖框期间画面定住
        }

        /// <summary>拖框取消/无效:恢复拖框前状态。</summary>
        public void CancelRegionCapture()
        {
            IsFrozen = _freezeRestore;
            _regionBase?.Dispose(); _regionBase = null;
        }

        /// <summary>松手:从按下瞬间的快照里裁出框选区域送编辑器。</summary>
        public void CaptureRegion(int x, int y, int w, int h)
        {
            IsFrozen = _freezeRestore;
            bool ownSrc = _regionBase != null;  
            var src = _regionBase ?? _frame;
            _regionBase = null;  
            if (src == null || src.Empty())
            {
                if (ownSrc) src?.Dispose();
                StatusText = "还没有画面:先抓取或开启实时预览";
                return;
            }

            x = Math.Clamp(x, 0, src.Width - 2);
            y = Math.Clamp(y, 0, src.Height - 2);
            w = Math.Clamp(w, 1, src.Width - x);
            h = Math.Clamp(h, 1, src.Height - y);

            Mat region;
            try
            {
                region = new Mat(src, new OpenCvSharp.Rect(x, y, w, h)).Clone();
            }
            finally
            {
                if (ownSrc) src.Dispose();  
            }

            _editorWindows.Show(region);
            StatusText = $"已框选截图 {w}×{h} → 编辑器";
        }


        // ============ 帧管线 ============
        private Mat? GrabOneFrame(bool toEditor = false, bool live = false)
        {
            try
            {
                var raw = _game.CaptureWindow();
                if (raw == null || raw.Empty())
                { raw?.Dispose(); SetStatus("未获取画面(请先在【启动】页开启截图引擎)"); return null; }

                string srcInfo = $"{raw.Width}×{raw.Height}";
                var norm = _render.SyncNteRenderFrame(raw);
                raw.Dispose();

                if (toEditor) return norm;

                var bgra = new Mat();
                Cv2.CvtColor(norm, bgra, ColorConversionCodes.BGR2BGRA);
                var app = Application.Current?.Dispatcher;
                if (app == null) { norm.Dispose(); bgra.Dispose(); return null; }

                app.Invoke(() =>
                {
                    // 竞态守卫:实时帧落地时若已进入冻结/框选,丢弃这帧,
                    // 保证按下鼠标之后屏幕画面与 _regionBase 永远是同一帧
                    if (live && IsFrozen) { norm.Dispose(); return; }

                    var old = _frame;
                    _frame = norm;
                    old?.Dispose();

                    if (_wb == null || _wb.PixelWidth != bgra.Width || _wb.PixelHeight != bgra.Height)
                        _wb = new WriteableBitmap(bgra.Width, bgra.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    _wb.WritePixels(new Int32Rect(0, 0, bgra.Width, bgra.Height),
                                    bgra.Data, bgra.Width * bgra.Height * 4, bgra.Width * 4);
                    FrameImage = _wb;
                    StatusText = $"1080P 帧 {bgra.Width}×{bgra.Height} · 来源 {srcInfo} · {DateTime.Now:HH:mm:ss}";
                });
                bgra.Dispose();
                return null;
            }
            catch (Exception ex) { SetStatus($"取帧失败: {ex.Message}"); return null; }
        }


        private void SetStatus(string text) =>
            Application.Current?.Dispatcher?.BeginInvoke(() => StatusText = text);

        // ============ 鼠标交互(预览图) ============
        public void OnFrameMouseMove(int px, int py)
        {
            if (_frame == null) return;
            px = Math.Clamp(px, 0, _frame.Width - 1);
            py = Math.Clamp(py, 0, _frame.Height - 1);
            var bgr = _frame.At<Vec3b>(py, px);
            int b = bgr.Item0, g = bgr.Item1, r = bgr.Item2;
            var (h, s, v) = RgbToHsvDamo(r, g, b);
            string info = $"({px},{py})  #{r:X2}{g:X2}{b:X2}\nHSV {h}.{s}.{v}   灰度 {(299 * r + 587 * g + 114 * b) / 1000}";

            if ((DateTime.Now - _lastMagUpdate).TotalMilliseconds >= 33)
            {
                _lastMagUpdate = DateTime.Now;
                using var mag = new Mat();
                Cv2.GetRectSubPix(_frame, new OpenCvSharp.Size(MagSrcPx, MagSrcPx), new Point2f(px + 0.5f, py + 0.5f), mag);
                using var big = new Mat();
                Cv2.Resize(mag, big, new OpenCvSharp.Size(MagDispPx, MagDispPx), 0, 0, InterpolationFlags.Nearest);
                _magnifier.Update(ImageHelper.MatToBitmapSource(big), info);
            }
        }

        public void PickAt(int px, int py)
        {
            if (_frame == null) { StatusText = "还没有画面:先抓取或开启实时预览"; return; }
            px = Math.Clamp(px, 0, _frame.Width - 1);
            py = Math.Clamp(py, 0, _frame.Height - 1);
            var bgr = _frame.At<Vec3b>(py, px);
            int b = bgr.Item0, g = bgr.Item1, r = bgr.Item2;
            var slot = SelectedPick ?? Picks.FirstOrDefault(p => !p.Enabled) ?? Picks[0];
            FillSlot(slot, $"{px},{py}", $"{r:X2}{g:X2}{b:X2}");
            StatusText = $"已写入第 {slot.Index} 组 · ({px},{py}) · #{slot.RgbHex}";
        }

        // ============  持久化 ============
        private void RefreshDamo()
        {
            DamoText = string.Join("|", Picks
                .Where(p => p.Enabled && !string.IsNullOrEmpty(p.RgbHex))
                .Select(p => $"{p.Logical},{p.RgbHex.ToLowerInvariant()}-{(string.IsNullOrWhiteSpace(p.Bias) ? "000000" : p.Bias.Trim().ToLowerInvariant())}"));
        }

        private void SaveSoon() { _saveDebounce.Stop(); _saveDebounce.Start(); }

        private void SavePicksNow() =>
            _configService.Update(c =>
            {
                c.DevTools.Picks = Picks.Select(p => new DevToolsPickState
                { Enabled = p.Enabled, Logical = p.Logical, RgbHex = p.RgbHex, Bias = p.Bias }).ToList();
            });

        private void LoadPicksFromConfig()
        {
            int n = Math.Min(_cfg.Picks.Count, Picks.Count);
            for (int i = 0; i < n; i++)
            {
                var s = _cfg.Picks[i];
                if (!string.IsNullOrEmpty(s.RgbHex))
                    FillSlot(Picks[i], s.Logical, s.RgbHex,
                             string.IsNullOrWhiteSpace(s.Bias) ? "000000" : s.Bias, s.Enabled);
            }
            RefreshDamo();
        }

        private void FillSlot(PickPointItem slot, string logical, string rgbHex, string bias = "000000", bool enabled = true)
        {
            var hex = rgbHex.TrimStart('#');
            if (hex.Length != 6) return;
            int r = Convert.ToInt32(hex[..2], 16), g = Convert.ToInt32(hex.Substring(2, 2), 16), b = Convert.ToInt32(hex[4..], 16);
            var (h, s, v) = RgbToHsvDamo(r, g, b);
            slot.Fill(logical, rgbHex.ToUpperInvariant(), $"{h}.{s}.{v}",
                      ((299 * r + 587 * g + 114 * b) / 1000).ToString(), bias, enabled);
        }

        private static (int h, int s, int v) RgbToHsvDamo(int r, int g, int b)
        {
            double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
            double max = Math.Max(rf, Math.Max(gf, bf));
            double min = Math.Min(rf, Math.Min(gf, bf));
            double delta = max - min;
            double hue = 0;
            if (delta > 0)
            {
                if (max == rf) hue = 60 * (((gf - bf) / delta) % 6);
                else if (max == gf) hue = 60 * ((bf - rf) / delta + 2);
                else hue = 60 * ((rf - gf) / delta + 4);
                if (hue < 0) hue += 360;
            }
            return ((int)Math.Round(hue / 360.0 * 255),
                    (int)Math.Round(max == 0 ? 0 : delta / max * 255),
                    (int)Math.Round(max * 255));
        }
    }

    public partial class PickPointItem : ObservableObject
    {
        public int Index { get; }
        [ObservableProperty] private bool _enabled;
        [ObservableProperty] private string _logical = "";
        [ObservableProperty] private string _rgbHex = "";
        [ObservableProperty] private string _hsvText = "";
        [ObservableProperty] private string _grayText = "";
        [ObservableProperty] private string _bias = "000000";
        [ObservableProperty] private Brush _swatch = Brushes.Black;

        public PickPointItem(int index) => Index = index;

        public void Fill(string logical, string rgbHex, string hsv, string gray, string bias, bool enabled)
        {
            Logical = logical; RgbHex = rgbHex; HsvText = hsv; GrayText = gray;
            Bias = bias; Enabled = enabled;
            try { Swatch = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + rgbHex)); }
            catch { }
        }

        public void Reset()
        {
            Logical = RgbHex = HsvText = GrayText = "";
            Bias = "000000"; Enabled = false; Swatch = Brushes.Black;
        }
    }
}
