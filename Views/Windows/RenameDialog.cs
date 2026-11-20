using System.Linq;

namespace TrustNTE.Views.Windows
{
    /// <summary>重命名输入框：FluentWindow + WPF-UI 控件，自动跟随应用深/浅主题。</summary>
    public static class RenameDialog
    {
        public static string? Show(string title, string initial)
        {
            var tb = new Wpf.Ui.Controls.TextBox { Text = initial, Margin = new System.Windows.Thickness(0, 0, 0, 20) };

            var ok = new Wpf.Ui.Controls.Button
            {
                Content = "确定",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                Width = 96,
                IsDefault = true,
                Margin = new System.Windows.Thickness(0, 0, 8, 0)
            };
            var cancel = new Wpf.Ui.Controls.Button { Content = "取消", Width = 96, IsCancel = true };

            var btns = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);

            var content = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(28, 4, 28, 20) };
            content.Children.Add(tb);
            content.Children.Add(btns);

            var titleBar = new Wpf.Ui.Controls.TitleBar { Title = title, ShowMinimize = false, ShowMaximize = false };

            var root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            System.Windows.Controls.Grid.SetRow(content, 1);
            root.Children.Add(titleBar);
            root.Children.Add(content);

            var owner = System.Windows.Application.Current.Windows
                .OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
                ?? System.Windows.Application.Current.MainWindow;

            var win = new Wpf.Ui.Controls.FluentWindow
            {
                Title = title,
                Owner = owner,
                Width = 400,
                SizeToContent = System.Windows.SizeToContent.Height,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                ExtendsContentIntoTitleBar = true,
                WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica,
                Content = root
            };
            win.SetResourceReference(System.Windows.Window.ForegroundProperty, "TextFillColorPrimaryBrush");

            string? result = null;
            ok.Click += (_, _) => { result = tb.Text?.Trim(); win.Close(); };
            tb.Focus();
            tb.SelectAll();
            win.ShowDialog();
            return string.IsNullOrEmpty(result) ? null : result;
        }
    }
}
