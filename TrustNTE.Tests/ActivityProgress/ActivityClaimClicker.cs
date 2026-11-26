using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using OpenCvSharp;
using TrustNTE.Tests.Core;
using TrustNTE.Tests.DebugTools;

namespace TrustNTE.Tests.Tools.ActivityProgress
{
    /// <summary>数字文本 ROI 配置项（X/Y/W/H 为 1080P 归一化坐标）</summary>
    public class ClickRoiItem
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public int Milestone { get; set; }
    }

    /// <summary>
    /// 自动领取配置（独立存储到 activity_click.json，不影响 activity_roi.json）。
    /// 判定信号：数字文本 #3A6FF6 亮蓝色 = 可领取；
    ///           已领取(对勾)/未达成(灰礼盒) 的数字是灰白色 → 检测不到蓝。
    /// 点击位置 = 数字 ROI 中心 + ClickOffsetY（往上偏移落到礼盒图标）。
    /// </summary>
    public class ClickConfig
    {
        /// <summary>5 个数字 ROI（你实测标定的值）</summary>
        public List<ClickRoiItem> NumberRois { get; set; } = new()
        {
            new ClickRoiItem { X = 662,  Y = 266, W = 42, H = 32, Milestone = 20  },
            new ClickRoiItem { X = 906,  Y = 270, W = 39, H = 27, Milestone = 40  },
            new ClickRoiItem { X = 1148, Y = 267, W = 35, H = 33, Milestone = 60  },
            new ClickRoiItem { X = 1379, Y = 271, W = 43, H = 27, Milestone = 80  },
            new ClickRoiItem { X = 1619, Y = 269, W = 50, H = 30, Milestone = 100 },
        };

        // ── 数字蓝色 #3A6FF6 的 HSV 阈值 ──
        // RGB(58,111,246) → HSV(H≈111, S≈195, V=246)
        public byte NumHMin { get; set; } = 100;
        public byte NumHMax { get; set; } = 125;
        public byte NumSMin { get; set; } = 120;
        public byte NumVMin { get; set; } = 150;

        /// <summary>蓝色像素占比 ≥ 该值 → 判定"该档可领取"</summary>
        public double BlueRatioMin { get; set; } = 0.10;

        /// <summary>点击点相对数字 ROI 中心的偏移（1080P 像素）。负数=往上。
        /// 默认 -55：数字中心往上 55px ≈ 礼盒图标中心。用模式[3]校准后调整。</summary>
        public int ClickOffsetY { get; set; } = -45;

        /// <summary>点击点水平偏移（一般 0，图标在数字正上方）</summary>
        public int ClickOffsetX { get; set; } = 0;

        /// <summary>两次点击之间的间隔 ms（等弹窗/动画结束）</summary>
        public int ClickIntervalMs { get; set; } = 800;

        /// <summary>游戏窗口标题（用于坐标映射）</summary>
        public string GameWindowTitle { get; set; } = "异环";

        private const string FilePath = "activity_click.json";

        public static ClickConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<ClickConfig>(json) ?? new ClickConfig();
                }
            }
            catch { }
            return new ClickConfig();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }

    /// <summary>Win32 鼠标模拟 + 游戏窗口坐标映射</summary>
    internal static class MouseSim
    {
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

        private const uint LEFTDOWN = 0x0002;
        private const uint LEFTUP = 0x0004;

        private struct RECT { public int Left, Top, Right, Bottom; }
        private struct POINT { public int X, Y; }

        public static void LeftClick(int screenX, int screenY)
        {
            SetCursorPos(screenX, screenY);
            Thread.Sleep(60);
            mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        public static void MoveOnly(int screenX, int screenY) => SetCursorPos(screenX, screenY);

        /// <summary>
        /// 1080P 归一化坐标 → 屏幕坐标。
        /// 优先找游戏窗口按客户区映射；找不到则假设全屏。
        /// </summary>
        public static (int sx, int sy) MapNormToScreen(double normX, double normY, string windowTitle)
        {
            int clientW = 1920, clientH = 1080, originX = 0, originY = 0;

            IntPtr hwnd = FindWindow(null, windowTitle);
            if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out var rc) && rc.Right > 100)
            {
                clientW = rc.Right;
                clientH = rc.Bottom;
                var pt = new POINT { X = 0, Y = 0 };
                if (ClientToScreen(hwnd, ref pt)) { originX = pt.X; originY = pt.Y; }
            }
            else
            {
                clientW = GetSystemMetrics(0);   // SM_CXSCREEN
                clientH = GetSystemMetrics(1);   // SM_CYSCREEN
            }

            int sx = originX + (int)Math.Round(normX * clientW / 1920.0);
            int sy = originY + (int)Math.Round(normY * clientH / 1080.0);
            return (sx, sy);
        }
    }

    /// <summary>
    /// 活跃度自动领取工具。
    /// 原理：礼盒是 GIF 模板匹配会失效，但下方数字是静态的，
    ///       且只有"已达成待领取"状态的数字才是亮蓝 #3A6FF6。
    ///       检测蓝色数字 → 点击位置往上偏移 → 点中礼盒领取。
    /// </summary>
    public sealed class ActivityClaimClicker : IDebugTool
    {
        public string Name => "活跃度自动领取";
        public string Description => "蓝色数字(#3A6FF6)定位可领节点, 上偏移点击礼盒";

        public void Run(TestCaptureDrone drone)
        {
            ActivityRoiCalibrator.GrabNormalizedFrame = () => drone.GetLatestFrame();

            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("══════════ 活跃度自动领取 ══════════");
                Console.WriteLine(" [1] 扫描检测 (只看不动鼠标, 验证蓝色数字判定)");
                Console.WriteLine(" [2] 重新标定 5 个数字 ROI");
                Console.WriteLine(" [3] 校准点击位置 (只移动鼠标不点击)");
                Console.WriteLine(" [4] 自动领取 ⚠ 会真实点击鼠标!");
                Console.WriteLine(" [0] 返回主菜单");
                Console.WriteLine("════════════════════════════════════");
                Console.Write("请输入编号: ");

                string? input = Console.ReadLine();
                if (input == "0" || string.IsNullOrWhiteSpace(input)) return;

                switch (input)
                {
                    case "1": ScanMode(drone); break;
                    case "2": CalibrateMode(drone); break;
                    case "3": MoveTestMode(drone); break;
                    case "4": AutoClaimMode(drone); break;
                }
            }
        }

        // ════════════════ 核心：蓝色数字检测 ════════════════

        /// <summary>单帧扫描结果</summary>
        private sealed record ScanItem(ClickRoiItem Roi, double BlueRatio, bool Claimable, Point2f ClickPointNorm);

        private static List<ScanItem> ScanFrame(Mat frame, ClickConfig cfg)
        {
            var results = new List<ScanItem>();
            int cw = frame.Width, ch = frame.Height;

            foreach (var r in cfg.NumberRois)
            {
                int x = Math.Clamp(r.X, 0, cw - r.W);
                int y = Math.Clamp(r.Y, 0, ch - r.H);
                var rect = new Rect(x, y, r.W, r.H);

                double ratio = 0;
                using (var roi = new Mat(frame, rect))
                using (var hsv = new Mat())
                using (var mask = new Mat())
                {
                    Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
                    Cv2.InRange(hsv,
                        new Scalar(cfg.NumHMin, cfg.NumSMin, cfg.NumVMin),
                        new Scalar(cfg.NumHMax, 255, 255),
                        mask);
                    ratio = Cv2.CountNonZero(mask) / (double)(r.W * r.H);
                }

                bool claimable = ratio >= cfg.BlueRatioMin;
                var clickPt = new Point2f(
                    r.X + r.W / 2f + cfg.ClickOffsetX,
                    r.Y + r.H / 2f + cfg.ClickOffsetY);

                results.Add(new ScanItem(r, ratio, claimable, clickPt));
            }
            return results;
        }

        // ════════════════ 模式 1：扫描检测 ════════════════

        private static void ScanMode(TestCaptureDrone drone)
        {
            var cfg = ClickConfig.Load();
            Console.WriteLine("扫描中, 按 ESC 退出。绿框=可领取(蓝数字) 红框=不可领, 十字=点击落点");

            while (true)
            {
                using var frame = drone.GetLatestFrame();
                if (frame == null) continue;

                var items = ScanFrame(frame, cfg);

                foreach (var it in items)
                {
                    var rect = new Rect(it.Roi.X, it.Roi.Y, it.Roi.W, it.Roi.H);
                    Cv2.Rectangle(frame, rect, it.Claimable ? Scalar.Lime : Scalar.Red, 2);
                    // 画出点击落点十字
                    var p = it.ClickPointNorm;
                    // 坐标用 new Point(x, y) 包成一个对象
                    Cv2.DrawMarker(frame, new OpenCvSharp.Point((int)p.X, (int)p.Y),
                                   it.Claimable ? Scalar.Magenta : Scalar.Gray,
                                   MarkerTypes.Cross, 20, 2);

                }

                var line = string.Join(" ", items.Select(t =>
                    $"[{t.Roi.Milestone}]{(t.Claimable ? "可领" : " -- ")}{t.BlueRatio,5:P0}"));
                Console.Write($"\r{line}   ");

                using var view = new Mat();
                Cv2.Resize(frame, view, new OpenCvSharp.Size(1280, 720));
                Cv2.ImShow("Claim Scanner", view);

                if (Cv2.WaitKey(50) == 27) break;
            }
            Cv2.DestroyAllWindows();
            Console.WriteLine();
        }

        // ════════════════ 模式 2：标定数字 ROI ════════════════

        private static void CalibrateMode(TestCaptureDrone drone)
        {
            if (!WaitFrame(drone, out var frame)) return;
            var cfg = ClickConfig.Load();

            if (cfg.NumberRois.Count == 0)
                cfg.NumberRois = Enumerable.Range(0, 5).Select(k =>
                    new ClickRoiItem { Milestone = (k + 1) * 20 }).ToList();

            Console.WriteLine("[标定] 框住每个档位下方的数字文本区域！");

            for (int i = 0; i < cfg.NumberRois.Count; i++)
            {
                var item = cfg.NumberRois[i];
                string title = $"Step{i + 1}/5: 框住 [{item.Milestone}] 的数字";
                var roi = Cv2.SelectROI(title, frame, true, false);
                Cv2.DestroyWindow(title);
                if (roi.Width <= 5) { Console.WriteLine($"[标定] [{item.Milestone}] 跳过"); continue; }
                item.X = roi.X; item.Y = roi.Y; item.W = roi.Width; item.H = roi.Height;
                Console.WriteLine($"[ROI {item.Milestone}] {roi.X},{roi.Y} {roi.Width}x{roi.Height}");
            }

            cfg.Save();
            frame.Dispose();
            Cv2.DestroyAllWindows();
            Console.WriteLine("[标定] 已保存到 activity_click.json");
        }

        // ════════════════ 模式 3：校准点击位置（只移动不点击）════════════════

        private static void MoveTestMode(TestCaptureDrone drone)
        {
            var cfg = ClickConfig.Load();

            using var frame = drone.GetLatestFrame();
            if (frame == null) { Console.WriteLine("抓帧失败"); return; }

            var items = ScanFrame(frame, cfg);
            var claimable = items.Where(t => t.Claimable).ToList();

            if (claimable.Count == 0)
            {
                Console.WriteLine("当前没有检测到可领取节点（无蓝色数字）。");
                return;
            }

            Console.WriteLine($"检测到 {claimable.Count} 个可领取节点，鼠标将依次移动到点击落点（不点击）。");
            Console.WriteLine("观察鼠标是否落在礼盒图标中心；不对就改 activity_click.json 的 ClickOffsetY。");
            Console.WriteLine("按 ESC 跳到下一个 / Ctrl+C 退出。");

            foreach (var it in claimable)
            {
                var (sx, sy) = MouseSim.MapNormToScreen(it.ClickPointNorm.X, it.ClickPointNorm.Y, cfg.GameWindowTitle);
                Console.Write($"[{it.Roi.Milestone}] 归一化落点({it.ClickPointNorm.X:F0},{it.ClickPointNorm.Y:F0}) → 屏幕({sx},{sy})，3秒后移动...");
                for (int s = 3; s > 0; s--) { Console.Write($"{s} "); Thread.Sleep(1000); }
                MouseSim.MoveOnly(sx, sy);
                Console.WriteLine(" 已移动。按任意键继续下一个。");
                while (!Console.KeyAvailable) Thread.Sleep(50);
                if (Console.ReadKey(true).Key == ConsoleKey.Escape) break;
            }
        }

        // ════════════════ 模式 4：自动领取（真实点击）════════════════

        private static void AutoClaimMode(TestCaptureDrone drone)
        {
            var cfg = ClickConfig.Load();

            Console.WriteLine();
            Console.WriteLine("⚠⚠⚠ 即将开始自动点击鼠标！⚠⚠⚠");
            Console.WriteLine("  1. 请把游戏窗口切到前台且不要遮挡");
            Console.WriteLine("  2. 过程中不要动鼠标");
            Console.WriteLine("  3. 控制台按 ESC 随时中止");
            Console.Write("输入 YES 确认开始: ");
            string? confirm = Console.ReadLine();
            if (confirm != "YES") { Console.WriteLine("已取消。"); return; }

            Console.WriteLine("3 秒后开始...");
            for (int s = 3; s > 0; s--) { Console.Write($"{s} "); Thread.Sleep(1000); }
            Console.WriteLine();

            int totalClicked = 0;

            while (true)
            {
                // ESC 中止检查
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                {
                    Console.WriteLine("\n[中止] 用户按 ESC。");
                    break;
                }

                using var frame = drone.GetLatestFrame();
                if (frame == null) { Thread.Sleep(200); continue; }

                var items = ScanFrame(frame, cfg);
                var targets = items.Where(t => t.Claimable)
                                   .OrderBy(t => t.Roi.Milestone)
                                   .ToList();

                if (targets.Count == 0)
                {
                    Console.Write("\r没有可领取节点，等待中...          ");
                    Thread.Sleep(500);
                    continue;
                }

                foreach (var t in targets)
                {
                    if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                    { Console.WriteLine("\n[中止] 用户按 ESC。"); return; }

                    var (sx, sy) = MouseSim.MapNormToScreen(t.ClickPointNorm.X, t.ClickPointNorm.Y, cfg.GameWindowTitle);
                    Console.WriteLine($"\r[领取] 档位[{t.Roi.Milestone}] 蓝={t.BlueRatio:P0} 点击屏幕({sx},{sy})   ");
                    MouseSim.LeftClick(sx, sy);
                    totalClicked++;
                    Thread.Sleep(cfg.ClickIntervalMs);
                }

                Console.WriteLine($"[本轮] 已点击 {targets.Count} 个，累计 {totalClicked}。重新扫描...");
                Thread.Sleep(500);
            }

            Console.WriteLine($"[结束] 共点击 {totalClicked} 次。");
        }

        private static bool WaitFrame(TestCaptureDrone drone, out Mat frame)
        {
            Console.WriteLine("[标定] 等待截图线程...");
            frame = null!;
            for (int i = 0; i < 50 && frame == null; i++)
            {
                frame = drone.GetLatestFrame();
                if (frame == null) Thread.Sleep(100);
            }
            if (frame == null) { Console.WriteLine("[标定] 抓帧失败。"); return false; }
            return true;
        }
    }
}
