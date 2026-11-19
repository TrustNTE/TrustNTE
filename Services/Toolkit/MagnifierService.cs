using System;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TrustNTE.Services.Toolkit
{
    /// <summary>
    /// 悬浮放大镜:独立置顶无边框小窗,整个窗可拖到屏幕任意位置(含主窗外/游戏画面上)。
    /// 内容由取色器预览/编辑器鼠标悬停推送;开关在开发者工具工具栏。
    /// </summary>
    public partial class MagnifierService : ObservableObject
    {
        private Views.MagnifierWindow? _window;

        [ObservableProperty] private ImageSource? _zoomImage;
        [ObservableProperty] private string _info = "";

        private bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set { if (SetProperty(ref _enabled, value) && !value) Hide(); }
        }

        /// <summary>悬停方(UI 线程事件)调用;内部兜底派发。未开启时直接忽略。</summary>
        public void Update(ImageSource? zoom, string info)
        {
            var app = Application.Current?.Dispatcher;
            if (app == null) return;
            app.BeginInvoke(() =>
            {
                if (!_enabled) return;
                EnsureShow();
                ZoomImage = zoom;
                Info = info;
            });
        }

        private void EnsureShow()
        {
            if (_window != null && _window.IsVisible) return;
            if (_window == null)
            {
                _window = new Views.MagnifierWindow { DataContext = this };
                _window.Left = 220; _window.Top = 180; // 首次出现位置;之后跟手拖动,Hide/Show 保留位置
            }
            _window.Show();
        }

        public void Hide()
        {
            var app = Application.Current?.Dispatcher;
            app?.BeginInvoke(() => _window?.Hide());
        }
    }
}
