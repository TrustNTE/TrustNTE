using System.Windows.Controls;
using System.Windows.Input;
using TrustNTE.ViewModels.Pages;

namespace TrustNTE.Views.Pages
{
    public partial class SchedulerPage : UserControl
    {
        public SchedulerPage(SchedulerViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        /// <summary>双击分组 → 重命名（必须把 SelectedGroup 传进去，传 null 会被命令直接拒绝）。</summary>
        private void GroupsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is SchedulerViewModel vm && vm.SelectedGroup != null)
                vm.RenameGroupCommand.Execute(vm.SelectedGroup);
        }
    }
}
