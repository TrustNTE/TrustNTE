using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using TrustNTE.Helpers;
using TrustNTE.Services;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Scripting
{
    /// <summary>找图结果（1080P 逻辑坐标）。CX/CY = 中心点，直接喂 automation.Click。</summary>
    public sealed class VisionMatch
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public int CX { get; set; }
        public int CY { get; set; }
        public override string ToString() => $"({CX},{CY}) {W}x{H}";
    }

    /// <summary>
    /// JS 视觉 API（vision 宿主对象）：截图/找图/取色/OCR/存图做模板。
    /// 所有坐标均为 1080P 逻辑坐标——帧经 NteRenderContextManager 归一化，
    /// 与 automation.Click 的坐标系完全一致：找到即点中。
    /// </summary>
    public sealed class JsVisionApi
    {
        private readonly IGameService _game;
        private readonly IAutomationContext _auto;
        private readonly NteRenderContextManager _render;
        private readonly IOcrService _ocr;

        /// <summary>停止检查（引擎每次执行时设置）：WaitFind 轮询据此提前退出。</summary>
        public Func<bool> StopRequested = () => false;

        private readonly string _tplDir;                       // UserData\Templates
        private readonly object _tplLock = new();
        private readonly Dictionary<string, Mat> _tplCache = new();

        public JsVisionApi(IGameService game, IAutomationContext auto,
            NteRenderContextManager render, IOcrService ocr)
        {
            _game = game; _auto = auto; _render = render; _ocr = ocr;
            _tplDir = LocateTemplateDir();   // ★v1.3.1 改：锚定算法替代 BaseDirectory 硬拼
            Directory.CreateDirectory(_tplDir);   // 已存在则为 no-op，保留兜底保证
        }

        // ★v1.3.1 新增：模板目录锚定——自动向上找 UserData\Templates，不再瞎猜 bin 树
        private static string LocateTemplateDir()
        {
            var candidates = new List<string>();
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            // ① 从 exe 位置向上最多 6 层找 UserData\Templates
            //    兼容 bin\Debug\net8.0\ 多层嵌套，也能锚到仓库根下的 UserData
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
                candidates.Add(Path.Combine(dir.FullName, "UserData", "Templates"));

            // ② 当前工作目录（从仓库根启动的场景）
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "UserData", "Templates"));

            foreach (var c in candidates)
                if (Directory.Exists(c))
                {
                    Console.WriteLine($"[JsVisionApi] 模板目录锚定: {c}");
                    return c;
                }

            // ③ 兜底：exe 旁创建，不崩，且把期望路径明示出来
            var fb = Path.Combine(AppContext.BaseDirectory, "UserData", "Templates");
            Directory.CreateDirectory(fb);
            Console.WriteLine($"[JsVisionApi] ⚠ 未找到 UserData\\Templates，已创建兜底: {fb}");
            return fb;
        }

        // ══════════ 内部管线 ══════════

        /// <summary>抓一帧并归一化到 1080P。瞬时截失败返回 null（轮询场景跳过本次），硬错误抛异常。</summary>
        private Mat? TryGrabFrame()
        {
            var hwnd = _auto.CurrentHwnd;
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("游戏窗口未绑定，无法截图");
            var raw = _game.CaptureWindowOf(hwnd);
            if (raw == null || raw.Empty()) return null;
            var norm = _render.SyncNteRenderFrame(raw);
            raw.Dispose();
            return norm;
        }

        private Mat GrabFrame() =>
            TryGrabFrame() ?? throw new InvalidOperationException("截图失败：请确认截图引擎已启动");

        /// <summary>加载灰度模板（防路径穿越，仅限 UserData\Templates，带缓存）。</summary>
        private Mat LoadTemplate(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("模板名为空");
            var full = Path.GetFullPath(Path.Combine(_tplDir, name));
            var rel = Path.GetRelativePath(_tplDir, full);
            if (rel.StartsWith("..") || Path.IsPathRooted(rel))
                throw new UnauthorizedAccessException($"[安全拦截] 模板路径越界: {name}");
            if (!File.Exists(full))
                throw new FileNotFoundException($"模板不存在: {full}");

            lock (_tplLock)
            {
                if (_tplCache.TryGetValue(rel, out var cached)) return cached;
                using var color = Cv2.ImRead(full, ImreadModes.Color);
                if (color.Empty()) throw new InvalidOperationException($"模板读取失败: {name}");
                var gray = new Mat();
                Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
                _tplCache[rel] = gray;
                return gray;
            }
        }

        /// <summary>清空模板缓存（每次脚本执行前调用——编辑模板后立即生效，不用重启）。</summary>
        public void ClearCache()
        {
            lock (_tplLock)
            {
                foreach (var m in _tplCache.Values) m.Dispose();
                _tplCache.Clear();
            }
        }

        // ══════════ JS API ══════════

        /// <summary>截当前画面存为 PNG 到 UserData\Templates（用于制作模板）。返回保存路径。</summary>
        public string SaveShot(string name = "debug.png")
        {
            var full = Path.Combine(_tplDir, Path.GetFileName(name)); // 只取文件名防穿越
            using var frame = GrabFrame();
            Cv2.ImWrite(full, frame);
            return $"UserData\\Templates\\{Path.GetFileName(name)}";
        }

        /// <summary>模板找图。找到返回 {X,Y,W,H,CX,CY}，没找到返回 null。</summary>
        public VisionMatch? Find(string template, double threshold = 0.8)
        {
            using var frame = GrabFrame();
            var tpl = LoadTemplate(template);
            bool ok = ImageMatcher.MatchMulti(frame, new List<Mat> { tpl },
                new Rect(0, 0, frame.Width, frame.Height), 1.0, threshold, out var rect);
            if (!ok) return null;
            return new VisionMatch
            {
                X = rect.X,
                Y = rect.Y,
                W = rect.Width,
                H = rect.Height,
                CX = rect.X + rect.Width / 2,
                CY = rect.Y + rect.Height / 2
            };
        }

        /// <summary>等待模板出现（轮询）。找到立即返回位置；超时/被停止返回 null。</summary>
        public VisionMatch? WaitFind(string template, int timeoutMs, int intervalMs = 200, double threshold = 0.8)
        {
            if (intervalMs < 50) intervalMs = 50;
            var tpl = LoadTemplate(template);                      // 模板错误立刻抛，不浪费轮询
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            while (Environment.TickCount64 < deadline)
            {
                if (StopRequested()) return null;
                using (var frame = TryGrabFrame())
                {
                    if (frame != null && ImageMatcher.MatchMulti(frame, new List<Mat> { tpl },
                        new Rect(0, 0, frame.Width, frame.Height), 1.0, threshold, out var rect))
                    {
                        return new VisionMatch
                        {
                            X = rect.X,
                            Y = rect.Y,
                            W = rect.Width,
                            H = rect.Height,
                            CX = rect.X + rect.Width / 2,
                            CY = rect.Y + rect.Height / 2
                        };
                    }
                }
                Thread.Sleep(intervalMs);
            }
            return null;
        }

        /// <summary>取像素颜色（1080P 坐标）→ "#RRGGBB"。</summary>
        public string Pixel(int x, int y)
        {
            using var frame = GrabFrame();
            x = Math.Clamp(x, 0, frame.Width - 1);
            y = Math.Clamp(y, 0, frame.Height - 1);
            var bgr = frame.At<Vec3b>(y, x);
            return $"#{bgr.Item2:X2}{bgr.Item1:X2}{bgr.Item0:X2}"; // BGR → RGB
        }

        /// <summary>OCR 整屏 → 识别文本（无文字返回空串）。</summary>
        public string Ocr()
        {
            using var frame = GrabFrame();
            return _ocr.RecognizeTextAsync(frame, System.Threading.CancellationToken.None)
                       .GetAwaiter().GetResult();
        }

        /// <summary>区域 OCR（1080P 坐标）→ 识别文本。比整屏快，优先用这个。</summary>
        public string OcrRegion(int x, int y, int w, int h)
        {
            using var frame = GrabFrame();
            var roi = new Rect(x, y, w, h).Intersect(new Rect(0, 0, frame.Width, frame.Height));
            if (roi.Width <= 0 || roi.Height <= 0) return "";
            using var crop = new Mat(frame, roi);
            return _ocr.RecognizeTextAsync(crop, System.Threading.CancellationToken.None)
                       .GetAwaiter().GetResult();
        }

        // ═══════════ 血条填充比例(无数字血条方案) ═══════════

        /// <summary>
        /// 暖色填充占比(0~1):统计 ROI 内 R−B > warmGap 的像素比例。
        /// 适用红/橙(含渐变)血条——整条渐变都是暖色,一次统计全覆盖;
        /// 空槽深蓝背景(R≈B或B>R)与白色描边/韧性条(R≈G≈B)天然排除。
        /// </summary>
        public double WarmRatio(int x, int y, int w, int h, int warmGap = 40)
        {
            using var frame = GrabFrame();
            var roi = new Rect(x, y, w, h).Intersect(new Rect(0, 0, frame.Width, frame.Height));
            if (roi.Width <= 0 || roi.Height <= 0) throw new ArgumentException($"ROI 越界: {x},{y},{w},{h}");

            using var crop = new Mat(frame, roi);
            long hit = 0, total = 0;
            for (int row = 0; row < crop.Rows; row++)
                for (int col = 0; col < crop.Cols; col++)
                {
                    var p = crop.At<Vec3b>(row, col);            // BGR
                    total++;
                    if (p.Item2 - p.Item0 > warmGap) hit++;      // R − B > gap → 暖色填充
                }
            return total == 0 ? 0 : (double)hit / total;
        }

        /// <summary>
        /// 指定色填充占比(0~1):统计 ROI 内接近 color 的像素比例(欧氏距离≤tolerance)。
        /// 适用纯色血条(绿/黄等)。渐变条别用这个,用 WarmRatio。
        /// </summary>
        public double BarRatio(int x, int y, int w, int h, string color = "#40FF40", int tolerance = 70)
        {
            using var frame = GrabFrame();
            var roi = new Rect(x, y, w, h).Intersect(new Rect(0, 0, frame.Width, frame.Height));
            if (roi.Width <= 0 || roi.Height <= 0) throw new ArgumentException($"ROI 越界: {x},{y},{w},{h}");

            var hex = (color ?? "#40FF40").TrimStart('#');
            if (hex.Length != 6) throw new ArgumentException("颜色格式应为 #RRGGBB");
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);

            using var crop = new Mat(frame, roi);
            long hit = 0, total = 0;
            for (int row = 0; row < crop.Rows; row++)
                for (int col = 0; col < crop.Cols; col++)
                {
                    var p = crop.At<Vec3b>(row, col);            // BGR
                    int dr = p.Item2 - r, dg = p.Item1 - g, db = p.Item0 - b;
                    total++;
                    if (dr * dr + dg * dg + db * db <= tolerance * tolerance) hit++;
                }
            return total == 0 ? 0 : (double)hit / total;
        }
    }
}
