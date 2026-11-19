using System.Windows.Controls;
using TrustNTE.ViewModels.Pages;

namespace TrustNTE.Views.Pages
{
    public partial class DpsAnalysisPage : UserControl
    {
        public DpsAnalysisPage(DpsAnalysisPageViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        /// <summary>风险层"下载 Npcap"链接:用系统默认浏览器打开官网。</summary>
        private void OnNpcapLink_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch { /* 打不开浏览器时静默忽略 */ }
        }
    }
}
