using System;
using System.Linq;
using OpenCvSharp;
using TrustNTE.Tests.Core;
using TrustNTE.Tests.DebugTools;

namespace TrustNTE.Tests.Tools.ActivityProgress
{
    public sealed class ActivityTool : IDebugTool
    {
        public string Name => "活跃度检测";
        public string Description => "活跃度进度条按段填充检测 / ROI 标定 / 数值反算";

        public void Run(TestCaptureDrone drone)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("  活跃度进度条检测 v3 (按段填充长度判定)");
            Console.WriteLine("========================================");
            Console.WriteLine(" [1] 实时检测 (读取本地已保存的 ROI)");
            Console.WriteLine(" [2] 重新标定 5 段进度条 ROI");
            Console.WriteLine("========================================");
            Console.Write("请输入编号: ");

            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) input = "1";

            ActivityRoiCalibrator.GrabNormalizedFrame = () => drone.GetLatestFrame();

            if (input == "2")
            {
                ActivityRoiCalibrator.Run();
                return;
            }

            var config = ActivityConfig.Load();
            using var detector = new ActivityDetector(config);

            Console.WriteLine("图例: 绿框=该段已满  黄框=进行中  红框=未开始。按 ESC 退出。");

            while (true)
            {
                using var frame = drone.GetLatestFrame();
                if (frame != null)
                {
                    var result = detector.Analyze(frame);
                    if (result.Segments.Length == 0) continue;

                    for (int i = 0; i < detector.LastRois.Length; i++)
                    {
                        // 绿=已满 黄=进行中 红=未开始
                        var color = result.Segments[i].Confirmed ? Scalar.Lime
                                  : result.Segments[i].FillRatio > 0.02 ? Scalar.Yellow
                                  : Scalar.Red;
                        Cv2.Rectangle(frame, detector.LastRois[i], color, 2);
                    }

                    var line = string.Join(" ",
                        result.Segments.Select(t =>
                            $"[{t.Milestone}]{(t.Confirmed ? "✔" : $"{t.FillRatio,3:P0}")}"));
                    Console.Write($"\r{line} | 活跃度≈{result.EstimatedValue,3}          ");

                    using var preview = new Mat();
                    Cv2.Resize(frame, preview, new OpenCvSharp.Size(1280, 720));
                    Cv2.ImShow("Game Preview", preview);

                    using var strip = detector.SnapshotMaskStrip();
                    if (strip != null) Cv2.ImShow("Fill Masks (20|40|60|80|100)", strip);
                }
                if (Cv2.WaitKey(10) == 27) break;
            }
            Cv2.DestroyAllWindows();
            Console.WriteLine();
        }
    }
}
