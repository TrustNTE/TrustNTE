using System.Windows;
using TrustNTE.ViewModels.Pages;
using TrustNTE.Views.Pages;

namespace TrustNTE.Views.Windows
{
    /// <summary>抽卡分析独立窗口（不占用主窗口页面区）。宿主复用 GachaAnalysisPage。</summary>
    public partial class GachaAnalyzerWindow : Window
    {
        public GachaAnalyzerWindow(GachaAnalysisPageViewModel viewModel)
        {
            InitializeComponent();
            Host.Content = new GachaAnalysisPage(viewModel);
        }

        private void OnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void OnClose_Click(object sender, RoutedEventArgs e)
            => Close();
    }
}
