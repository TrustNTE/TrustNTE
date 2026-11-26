using System;
using OpenCvSharp;
using TrustNTE.Tests.Core;
using TrustNTE.Tests.DebugTools;

namespace TrustNTE.Tests.Tools.QEnergy
{
    /// <summary>Q 能量检测工具（逻辑从原 Program.Main 平移，实现类未做任何改动）</summary>
    public sealed class QEnergyTool : IDebugTool
    {
        public string Name => "Q 能量检测 v5";
        public string Description => "Q 能量绝对亮度检测 / ROI 标定";

        public void Run(TestCaptureDrone drone)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("  Q 能量 绝对亮度检测 v5 (最终版)");
            Console.WriteLine("========================================");
            Console.WriteLine(" [1] 实时检测 (读取本地已保存的 ROI)");
            Console.WriteLine(" [2] 重新标定 ROI (全屏拖框)");
            Console.WriteLine("========================================");
            Console.Write("请输入编号: ");

            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) input = "1";

            QEnergyRoiCalibrator.GrabNormalizedFrame = () => drone.GetLatestFrame();

            if (input == "2")
            {
                QEnergyRoiCalibrator.Run();
                return;
            }

            var config = new QEnergyConfig
            {
                RoiX = 1767,
                RoiY = 915,
                RoiWidth = 111,
                RoiHeight = 111,
                BrightReadyRatio = 0.15
            };

            using var detector = new QEnergyDetector(config);
            Console.WriteLine("按 ESC 键退出预览。");

            while (true)
            {
                using var frame = drone.GetLatestFrame();
                if (frame != null)
                {
                    var result = detector.Analyze(frame);
                    var roi = detector.LastRoi;
                    Cv2.Rectangle(frame, roi, Scalar.Red, 2);
                    using var roiMat = new Mat(frame, roi);
                    using var maskMat = detector.SnapshotLastMask();
                    using var roiBig = new Mat();
                    using var maskBig = new Mat();
                    Cv2.Resize(roiMat, roiBig, new Size(roiMat.Width * 3, roiMat.Height * 3));
                    if (maskMat != null) Cv2.Resize(maskMat, maskBig, new Size(maskMat.Width * 3, maskMat.Height * 3));

                    string state = result.Confirmed ? "✔ 满能量" : "✘ 未满";
                    Console.Write($"\r亮度占比: {result.BrightRatio,6:P1} | 状态: {state}          ");

                    using var preview = new Mat();
                    Cv2.Resize(frame, preview, new Size(1280, 720));
                    Cv2.ImShow("Game Preview", preview);
                    Cv2.ImShow("Q ROI (放大3倍)", roiBig);
                    if (maskMat != null) Cv2.ImShow("Q Bright Mask", maskBig);
                }
                if (Cv2.WaitKey(10) == 27) break;
            }
            Cv2.DestroyAllWindows();
        }
    }
}
