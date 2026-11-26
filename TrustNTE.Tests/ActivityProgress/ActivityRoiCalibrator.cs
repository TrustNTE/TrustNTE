using System;
using System.Linq;
using OpenCvSharp;

namespace TrustNTE.Tests.Tools.ActivityProgress
{
    /// <summary>
    /// 活跃度 ROI 拖框标定工具（进度条段版本）。
    /// 流程：等待截图 → 依次拖 5 段进度条 ROI → 保存 JSON → 实时验证。
    /// </summary>
    public static class ActivityRoiCalibrator
    {
        /// <summary>由外部（ActivityTool）注入的截图函数</summary>
        public static Func<Mat?> GrabNormalizedFrame = () => null;

        public static void Run()
        {
            Console.WriteLine("[标定] 正在等待后台截图线程就绪...");

            Mat? frame = null;
            int retry = 0;
            while (frame == null && retry < 50)
            {
                frame = GrabNormalizedFrame();
                if (frame == null) System.Threading.Thread.Sleep(100);
                retry++;
            }

            if (frame == null)
            {
                Console.WriteLine("[标定] 抓帧失败（等待超时），请检查游戏画面是否正常。");
                return;
            }

            var cfg = ActivityConfig.Load();

            Console.WriteLine("[标定] 提示：每段框住两个节点之间的进度条本体（青色区域），不要框到图标！");

            for (int i = 0; i < cfg.Rois.Count; i++)
            {
                var item = cfg.Rois[i];
                string title = $"Step{i + 1}/5: 框住 [{item.Milestone}] 档位的进度条段, 回车确认, ESC 跳过";

                var roi = Cv2.SelectROI(title, frame, showCrosshair: true, fromCenter: false);
                Cv2.DestroyWindow(title);

                if (roi.Width <= 5 || roi.Height <= 5)
                {
                    Console.WriteLine($"[标定] 段[{item.Milestone}] 未选择，保留旧值");
                    continue;
                }

                item.X = roi.X;
                item.Y = roi.Y;
                item.W = roi.Width;
                item.H = roi.Height;
                Console.WriteLine($"[ROI {item.Milestone}] X={roi.X} Y={roi.Y} W={roi.Width} H={roi.Height}  ← 已写入");
            }

            cfg.Save();
            Console.WriteLine("[标定] 全部 ROI 已保存到 activity_roi.json");

            frame.Dispose();
            RunVerify(cfg);
        }

        private static void RunVerify(ActivityConfig cfg)
        {
            using var det = new ActivityDetector(cfg);
            string verifyWin = "Step6: 验证 (绿=满 黄=进行中 红=未开始, ESC 退出)";
            Cv2.NamedWindow(verifyWin, WindowFlags.AutoSize);

            Console.WriteLine("[验证] 按 ESC 退出。");

            while (true)
            {
                using var f = GrabNormalizedFrame();
                if (f == null) break;

                var r = det.Analyze(f);
                if (r.Segments.Length == 0) break;

                for (int i = 0; i < det.LastRois.Length; i++)
                {
                    var color = r.Segments[i].Confirmed ? Scalar.Lime
                              : r.Segments[i].FillRatio > 0.02 ? Scalar.Yellow
                              : Scalar.Red;
                    Cv2.Rectangle(f, det.LastRois[i], color, 2);
                }

                using (var view = new Mat())
                {
                    Cv2.Resize(f, view, new OpenCvSharp.Size(1280, 720));
                    Cv2.ImShow(verifyWin, view);
                }

                using var strip = det.SnapshotMaskStrip();
                if (strip != null) Cv2.ImShow("Fill Masks (20|40|60|80|100)", strip);

                var line = string.Join(" ",
                    r.Segments.Select(t =>
                        $"[{t.Milestone}]{(t.Confirmed ? "✔" : $"{t.FillRatio,3:P0}")}"));
                Console.Write($"\r{line} | 活跃度≈{r.EstimatedValue,3}   ");

                if (Cv2.WaitKey(50) == 27) break;
            }

            Cv2.DestroyAllWindows();
            Console.WriteLine();
        }
    }
}
