using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using TrustNTE.ViewModels.Pages;

namespace TrustNTE.Views.Pages
{
    /// <summary>DPS 统计页(独立模块 · 三视图:风险告知 / 采集中 / 角色详情)。</summary>
    public partial class DpsAnalysisPage : UserControl
    {
        public DpsAnalysisPage(DpsAnalysisPageViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void OnNpcapLink_Click(object sender, RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        }
    }
}
