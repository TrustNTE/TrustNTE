using System.Windows.Controls;
using TrustNTE.ViewModels.Pages;

namespace TrustNTE.Views.Pages
{
    public partial class JsScriptPage : UserControl
    {
        public JsScriptPage(JsScriptViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
