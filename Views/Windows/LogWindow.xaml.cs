using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TrustNTE.Models;
using TrustNTE.Services.Interfaces;
using TrustNTE.ViewModels;
using TrustNTE.Views.Controls;
using Wpf.Ui.Controls;

// 别名：GlobalUsings 引入了 WinForms，RadioButton 撞名
using RadioButton = System.Windows.Controls.RadioButton;

namespace TrustNTE.Views
{
    public partial class LogWindow : FluentWindow
    {
        private readonly LogWindowViewModel _viewModel;
        private readonly IConfigService _configService = App.ServiceProvider.GetRequiredService<IConfigService>();
        private readonly ILogService _logService = App.ServiceProvider.GetRequiredService<ILogService>();

        public LogWindow(LogWindowViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = viewModel;

            // 开发者模式初始化：读配置 + 兜底推送（不依赖事件是否触发）
            bool devMode = _configService.Load().IsDeveloperMode;
            DevToggle.IsChecked = devMode;
            _logService.VerboseEnabled = devMode || Debugger.IsAttached;
            if (devMode) ChipAll.IsChecked = true;
            else ChipInfo.IsChecked = true;

            foreach (var log in _viewModel.Logs)
                MainConsole.EnqueueLine(log);
        }

        // 供 WindowService 调用的接收日志接口
        public void ReceiveLog(string message) => MainConsole.EnqueueLine(message);

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.Logs.Clear();
            MainConsole.Clear();
        }

        // 复制 = 窗口缓冲全量（共享集合只有 200 条，窗口缓冲 2000 条）
        private void CopyButton_Click(object sender, RoutedEventArgs e)
            => _viewModel.ShowCopyFeedback(MainConsole.CopyAllBuffered());

        private void Chip_Checked(object sender, RoutedEventArgs e)
        {
            if (MainConsole == null) return;
            // XAML 解析期"全部"默认 Checked 会提前触发
            if (sender is RadioButton rb && rb.Tag is string s)
            {
                // ★ DEBUG专用chip：Tag=DebugOnly → 只看DEBUG级条目
                if (s == "DebugOnly")
                {
                    MainConsole.ApplyMinLevel(LogEntryLevel.Debug, debugOnly: true);
                    return;
                }
                if (Enum.TryParse<LogEntryLevel>(s, out var lvl))
                    MainConsole.ApplyMinLevel(lvl);
            }
        }

        private void DevToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (MainConsole == null) return;
            bool on = DevToggle.IsChecked == true;
            var cfg = _configService.Load();
            if (cfg.IsDeveloperMode != on)
            {
                cfg.IsDeveloperMode = on;
                _configService.Save(cfg);
            }
            _logService.VerboseEnabled = on || Debugger.IsAttached;

            // 开=显示全部；关=立即藏掉存量 DEBUG 行
            if (on) ChipAll.IsChecked = true;
            else ChipInfo.IsChecked = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.Dispose();
            (Owner ?? Application.Current.MainWindow)?.Activate();
            base.OnClosed(e);
        }
    }
}
