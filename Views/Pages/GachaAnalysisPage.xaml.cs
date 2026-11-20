using System;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using TrustNTE.ViewModels.Pages;

namespace TrustNTE.Views.Pages
{
    public partial class GachaAnalysisPage : UserControl
    {
        public GachaAnalysisPage(GachaAnalysisPageViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        /// <summary>
        /// Npcap 官方下载链接：只允许跳外部默认浏览器。
        /// ★ 必须先 e.Handled = true 掐断 Hyperlink 默认行为——
        ///   否则 NavigationView 内部 Frame 会就地加载网页（IE 内核渲染 + 脚本错误弹窗）。
        /// </summary>
        private void OnNpcapLink_Click(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true; 

            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                if (DataContext is GachaAnalysisPageViewModel vm)
                    vm.AppendLog($"无法打开浏览器：{ex.Message}");
            }
        }
    }
}
