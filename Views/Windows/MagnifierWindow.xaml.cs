using System.Windows;
using System.Windows.Input;

namespace TrustNTE.Views
{
    /// <summary>悬浮放大镜窗:整窗可拖(DragMove),位置由用户自由摆放。</summary>
    public partial class MagnifierWindow : Window
    {
        public MagnifierWindow() => InitializeComponent();

        private void DragBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1) { try { DragMove(); } catch { /* 非按下态忽略 */ } }
        }
    }
}
