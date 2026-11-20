using System;
using System.Windows;
using TrustNTE.ViewModels;
using TrustNTE.Views;

namespace TrustNTE.Services.Toolkit
{
    /// <summary>开发者工具独立窗口:防重复打开 + 主窗可见时强绑 Owner(照 WindowService 模式)。</summary>
    public class DevToolsWindowService
    {
        private DevToolsWindow? _window;
        private readonly DevToolsWindowViewModel _viewModel;

        public DevToolsWindowService(DevToolsWindowViewModel viewModel) => _viewModel = viewModel;

        public void ShowDevTools()
        {

            if (!EngineGuard.EnsureReady()) return;   // 没开引擎:弹 StartErrorWindow,点超链接跳【启动】页;开了:往下走


            if (_window == null || !_window.IsVisible)
            {
                _window = new DevToolsWindow { DataContext = _viewModel };
                if (Application.Current?.MainWindow is { IsVisible: true } owner)
                {
                    _window.Owner = owner;
                    _window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                else
                {
                    _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
                _window.Closed += (_, _) => _window = null;
                _window.Show();
            }
            else
            {
                _window.Activate();
            }
        }
    }
}
