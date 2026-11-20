using System.Windows;
using OpenCvSharp;
using TrustNTE.ViewModels;
using TrustNTE.Views;

namespace TrustNTE.Services.Toolkit
{
    /// <summary>
    /// 图像编辑器独立窗口( 样式):单例管理。
    /// 【截图】传新帧进来 / 【编辑】复开继续编;VM 的 CloseRequested → 关窗(保存返回/放弃更改)。
    /// </summary>
    public class ImageEditorWindowService
    {
        private ImageEditorWindow? _window;
        private readonly ScreenshotEditorViewModel _vm;

        public ImageEditorWindowService(ScreenshotEditorViewModel vm)
        {
            _vm = vm;
            _vm.CloseRequested += () => _window?.Close(); // 构造时订阅一次,窗口换了几轮都有效
        }

        public void Show(Mat? initialBgr)
        {
            if (initialBgr != null) _vm.LoadFromFrame(initialBgr); // 所有权移交编辑器
            if (_window == null || !_window.IsVisible)
            {
                _window = new ImageEditorWindow { DataContext = _vm };
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
