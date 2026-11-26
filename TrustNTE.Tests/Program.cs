using System;
using System.Text;
using OpenCvSharp;
using TrustNTE.Tests.Core;
using TrustNTE.Tests.DebugTools;
using TrustNTE.Tests.Tools.ActivityProgress;
using TrustNTE.Tests.Tools.QEnergy;

namespace TrustNTE.Tests
{
    public class Program
    {
        // ═══════════ 工具注册表：新增工具 = 在这里加一行 ═══════════
        private static readonly IDebugTool[] Tools =
        {
            new QEnergyTool(),      // Q 能量检测
            new ActivityTool(),     // 活跃度进度条检测
            new ActivityClaimClicker(), //活跃度执行情况
            // new XxxTool(),       // ← 以后新工具在这里加一行
        };

        [STAThread]
        public static void Main(string[] args)
        {
            // 强制控制台使用 UTF-8 编码，解决 Emoji 和状态字符乱码
            Console.OutputEncoding = Encoding.UTF8;

            Console.WriteLine("========================================");
            Console.WriteLine("  TrustNTE 调试工具集");
            Console.WriteLine("========================================");
            for (int i = 0; i < Tools.Length; i++)
                Console.WriteLine($" [{i + 1}] {Tools[i].Name,-16} {Tools[i].Description}");
            Console.WriteLine(" [0] 退出");
            Console.WriteLine("========================================");
            Console.Write("请输入编号: ");

            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input) || input == "0") return;
            if (!int.TryParse(input, out int idx) || idx < 1 || idx > Tools.Length)
            {
                Console.WriteLine("无效编号。");
                return;
            }

            // 统一启动截图无人机，传给工具
            using var drone = new TestCaptureDrone();
            if (!drone.BindAndStart(50))
            {
                Console.WriteLine("❌ 启动失败，请确保异环游戏已运行。");
                return;
            }

            try
            {
                Tools[idx - 1].Run(drone);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n工具执行异常: {ex.Message}");
            }
            finally
            {
                Cv2.DestroyAllWindows();
            }
        }
    }
}
