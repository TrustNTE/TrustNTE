using System.Windows;
using TrustNTE.ViewModels.Pages;
using TrustNTE.Views.Pages;

namespace TrustNTE.Views.Windows
{
    /// <summary>DPS 统计独立窗口(不占用主窗口页面区,与抽卡分析窗口同模式但独立模块)。</summary>
    public partial class DpsAnalyzerWindow : Window
    {
        public DpsAnalyzerWindow(DpsAnalysisPageViewModel viewModel)
        {
            InitializeComponent();
            Host.Content = new DpsAnalysisPage(viewModel);
        }

        private void OnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void OnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
