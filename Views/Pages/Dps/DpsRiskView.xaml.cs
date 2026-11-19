using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace TrustNTE.Views.Pages
{
    public partial class DpsRiskView : UserControl
    {
        public DpsRiskView() => InitializeComponent();

        private void OnNpcapLink_Click(object sender, RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            e.Handled = true;
        }
    }
}
