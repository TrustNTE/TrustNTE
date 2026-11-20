using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TrustNTE.Update
{
    public partial class UpdateWindow : Window
    {
        private bool _isUpdating = false;
        private bool _updateFailed = false;

        public UpdateWindow()
        {
            InitializeComponent();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdating)
            {
                MessageBox.Show("正在更新中，请勿关闭！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            System.Windows.Application.Current.Shutdown();
        }

        private void ActionBtn_Click(object sender, RoutedEventArgs e)
        {
            string text = ActionBtn.Content?.ToString() ?? "";

            // ★ 防呆：没参数直接双击打开的，点击直接退出
            if (text == "关 闭")
            {
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // ★ 更新失败后的兜底：按钮变成"启 动"，直接启动主程序（原代码这个按钮点了没反应，顺手修了）
            if (text == "启 动")
            {
                LaunchMainApp();
                return;
            }

            BeginUpdate();
        }

        public async void BeginUpdate()
        {
            if (_isUpdating || _updateFailed) return;

            _isUpdating = true;
            ActionBtn.IsEnabled = false;
            ActionBtn.Visibility = Visibility.Collapsed;    // ★ 下载中隐藏底部按钮（解决灰按钮挂着的问题）
            SourcePanel.Visibility = Visibility.Collapsed;
            DownloadProgress.Visibility = Visibility.Visible;

            bool success = await Program.StartUpdateAsync(this);

            if (!success)
            {
                _isUpdating = false;
                _updateFailed = true;
                DownloadProgress.Visibility = Visibility.Collapsed;
                ActionBtn.Content = "启 动";
                ActionBtn.Visibility = Visibility.Visible;  // ★ 失败兜底：还能手动启动主程序
                ActionBtn.IsEnabled = true;
            }
        }

        public void InitSources(string? ghUrl, string? txUrl, Action<string> onSourceChanged)
        {
            if (!string.IsNullOrWhiteSpace(txUrl))
            {
                SourcePanel.Visibility = Visibility.Visible;
                if (!string.IsNullOrWhiteSpace(ghUrl))
                {
                    RbGithub.Checked += (s, e) => onSourceChanged(ghUrl);
                }
                else
                {
                    RbGithub.IsEnabled = false;
                    RbTencent.IsChecked = true;
                    onSourceChanged(txUrl);
                }
                RbTencent.Checked += (s, e) => onSourceChanged(txUrl);
            }
        }

        public void UpdateStatus(string message, bool isError = false)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = message;
                StatusText.Foreground = isError ? new SolidColorBrush(Color.FromRgb(255, 82, 82)) : Brushes.White;
            });
        }

        public void UpdateProgress(double value)
        {
            Dispatcher.Invoke(() => DownloadProgress.Value = value);
        }

        // ★ 完成态：下载+替换成功后由 Program.cs 调用，显示"启动/退出"让玩家自己决定
        public void ShowDoneState(bool updated, string? version = null)
        {
            StatusText.Visibility = Visibility.Collapsed;
            SourcePanel.Visibility = Visibility.Collapsed;
            DownloadProgress.Visibility = Visibility.Collapsed;
            ActionBtn.Visibility = Visibility.Collapsed;

            DonePanel.Visibility = Visibility.Visible;
            DoneVersionText.Text = updated
                ? (string.IsNullOrEmpty(version) ? "您已更新完毕，当前已是最新版本" : $"已更新到 v{version}，当前已是最新版本")
                : "您已安装最新版本，无需更新";
        }

        private void BtnLaunch_Click(object sender, RoutedEventArgs e) => LaunchMainApp();

        private void BtnExit_Click(object sender, RoutedEventArgs e)
            => System.Windows.Application.Current.Shutdown();

        private void LaunchMainApp()
        {
            try
            {
                var mainExe = Path.Combine(AppContext.BaseDirectory, "TrustNTE.exe");
                if (!File.Exists(mainExe))
                {
                    MessageBox.Show(this, "未找到 TrustNTE.exe，请到安装目录手动启动。", "TrustNTE",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo(mainExe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"启动主程序失败：{ex.Message}", "TrustNTE",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
