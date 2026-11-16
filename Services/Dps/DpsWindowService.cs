using System.Linq;
using System.Windows;
using TrustNTE.Services.Interfaces;
using TrustNTE.ViewModels.Pages;
using TrustNTE.Views.Windows;

namespace TrustNTE.Services.Dps
{
    /// <summary>
    /// DPS 统计独立窗口服务:防重复打开、强绑 Owner 防置顶失效。
    /// 与抽卡分析 GachaAnalyzerWindow 同模式,但独立建类,不占用工具箱模块。
    /// </summary>
    public class DpsWindowService : IDpsWindowService
    {
        private readonly DpsAnalysisPageViewModel _vm;

        public DpsWindowService(DpsAnalysisPageViewModel vm) => _vm = vm;

        public void ShowDpsAnalyzer()
        {
            var existing = Application.Current?.Windows.OfType<DpsAnalyzerWindow>().FirstOrDefault();
            if (existing != null) { existing.Activate(); return; }   // 防重复打开

            var win = new DpsAnalyzerWindow(_vm);
            if (Application.Current?.MainWindow is { IsLoaded: true } owner) win.Owner = owner;
            win.Show();
        }
    }
}
