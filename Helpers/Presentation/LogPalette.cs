using System.Windows.Media;
using TrustNTE.Models;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace TrustNTE.Helpers
{
    public static class LogPalette
    {
        private static Brush Red = Frozen("#FF5252");
        private static Brush Orange = Frozen("#FFA940");
        private static Brush Cyan = Frozen("#FF00FF");     // 鼠标：洋红
        private static Brush Purple = Frozen("#00FF00");   // 键盘：亮绿
        private static Brush Gray = Frozen("#7A828E");     // 高频：灰蓝
        private static Brush White = Frozen("#00FFFF");    // 默认文本：青色
        private static Brush DebugGray = Frozen("#8A8F98"); // 调试诊断：灰暗

        private static Brush Frozen(string hex)
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                return b;
            }
            catch
            {
                var b = new SolidColorBrush(Colors.White);
                b.Freeze();
                return b;
            }
        }

        public static void UpdateFromConfig(AppConfig config)
        {
            Red = Frozen(config.LogColorError);
            Orange = Frozen(config.LogColorGuard);
            Cyan = Frozen(config.LogColorMouse);
            Purple = Frozen(config.LogColorKeyboard);
            Gray = Frozen(config.LogColorHighFreq);
            White = Frozen(config.LogColorDefault);
            DebugGray = Frozen(config.LogColorDebug);
        }

        public static void ResetToDefault(AppConfig config)
        {
            config.LogColorDefault = "#00FFFF";
            config.LogColorError = "#FF5252";
            config.LogColorGuard = "#FFA940";
            config.LogColorHighFreq = "#7A828E";
            config.LogColorMouse = "#FF00FF";
            config.LogColorKeyboard = "#00FF00";
            config.LogColorDebug = "#8A8F98";
            UpdateFromConfig(config);
        }

        public static Brush Pick(string line)
        {
            if (string.IsNullOrEmpty(line)) return White;

            if (line.Contains("[ERROR]")) return Red;
            if (line.Contains("[WARN]")) return Orange;

            // 调试诊断：标签优先于关键词，DEBUG 行即使含“失败/错误”也保持灰暗
            if (line.Contains("[DEBUG]")) return DebugGray;

            if (line.Contains("错误") || line.Contains("失败") || line.Contains("异常") || line.Contains("找不到") || line.Contains("ERROR"))
                return Red;

            if (line.Contains("[守护]") || line.Contains("[监管器]"))
                return Orange;

            if (line.Contains("[钓鱼]") || line.Contains("[自动钓鱼]"))
                return White;

            if (line.Contains("心跳") || line.Contains("控鱼") || line.Contains("击发按键") || line.Contains("[按键发送]") || line.Contains("平A") || line.Contains("状态切换"))
                return Gray;

            if (line.Contains("左键") || line.Contains("右键") || line.Contains("点击") || line.Contains("Click"))
                return Cyan;

            if (line.Contains("键 [") || line.Contains("按下") || line.Contains("Key"))
                return Purple;

            return White;
        }
    }
}
