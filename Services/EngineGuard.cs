using System;
using System.Linq;
using System.Reflection;

namespace TrustNTE.Services
{
    /// <summary>
    /// 引擎就绪守卫（一行调用）：截图引擎没开 → 弹 StartErrorWindow 引导，返回 false。
    /// 用法：if (!EngineGuard.EnsureReady()) return;
    /// </summary>
    public static class EngineGuard
    {
        private static Func<bool>? _probe;
        private static bool _probeResolved;

        /// <summary>就绪返回 true；未就绪弹窗引导并返回 false。</summary>
        public static bool EnsureReady()
        {
            var automation = (AutomationService?)App.ServiceProvider.GetService(typeof(AutomationService));
            if (automation == null) { ShowGuide(); return false; }

            if (IsEngineRunning(automation)) return true;

            ShowGuide();
            return false;
        }

        private static bool IsEngineRunning(AutomationService automation)
        {
            if (!_probeResolved)
            {
                _probe = FindBoolProbe(automation, automation.GetType());
                if (_probe == null)
                {
                    var log = App.ServiceProvider.GetService(typeof(TrustNTE.Services.Interfaces.ILogService))
                              as TrustNTE.Services.Interfaces.ILogService;
                    log?.LogWarning("[EngineGuard] 未在 AutomationService 上识别到引擎运行状态属性（含 run/captur/start 的 bool），守卫放行。");
                }
                _probeResolved = true;
            }
            return _probe?.Invoke() ?? true;   // 探测不到 → 放行（宁可不拦，不可误拦）
        }

        /// <summary>反射找“引擎是否在跑”的 bool 属性/字段：名字含 run / captur / start（忽略大小写），排除否定词。</summary>
        private static Func<bool>? FindBoolProbe(object automation, Type t)
        {
            var bad = new[] { "not", "stop", "cancel", "disable", "exit", "close", "error", "fail" };
            bool NameOk(string n) =>
                (n.Contains("run", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("captur", StringComparison.OrdinalIgnoreCase)
                 || n.Contains("start", StringComparison.OrdinalIgnoreCase))
                && !bad.Any(b => n.Contains(b, StringComparison.OrdinalIgnoreCase));

            var prop = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.PropertyType == typeof(bool) && p.CanRead && NameOk(p.Name));
            if (prop != null)
            {
                var pi = prop;
                return () => (bool)pi.GetValue(automation)!;
            }

            var field = t.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType == typeof(bool) && NameOk(f.Name));
            if (field != null) return () => (bool)field.GetValue(automation)!;

            return null;
        }

        /// <summary>弹「启动错误」窗口（朕已阅 / 前往截图引擎配置），失败兜底 MessageBox。</summary>
        private static void ShowGuide()
        {
            try
            {
                var win = new TrustNTE.Views.Windows.StartErrorWindow
                { Owner = System.Windows.Application.Current.MainWindow };
                win.ShowDialog();
            }
            catch
            {
                System.Windows.MessageBox.Show(
                    "截图引擎未启动，请先到「启动」页开启截图引擎。",
                    "启动错误",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
    }
}
