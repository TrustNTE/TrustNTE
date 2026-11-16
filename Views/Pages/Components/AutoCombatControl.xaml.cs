using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TrustNTE.ViewModels.Pages;

namespace TrustNTE.Views.Pages.Components
{
    public partial class AutoCombatControl : UserControl
    {
        public AutoCombatControl()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => WirePickers();
        }

        private void WirePickers()
        {
            if (DataContext is not RealTimePageViewModel vm) return;
            P1.UsedByOthersProvider = () => Others(vm, 1);
            P2.UsedByOthersProvider = () => Others(vm, 2);
            P3.UsedByOthersProvider = () => Others(vm, 3);
            P4.UsedByOthersProvider = () => Others(vm, 4);
        }

        private static IEnumerable<string> Others(RealTimePageViewModel vm, int self)
        {
            string?[] all = { vm.TeamSlot1, vm.TeamSlot2, vm.TeamSlot3, vm.TeamSlot4 };
            for (int i = 0; i < 4; i++)
                if (i != self - 1 && !string.IsNullOrWhiteSpace(all[i])) yield return all[i]!;
        }

        private void SlotPicker_Committed(object sender, string? e)
        {
            if (DataContext is RealTimePageViewModel vm && sender is FrameworkElement fe && fe.Tag is string tag)
                vm.HandleSlotCommitted(int.Parse(tag), e);
        }
    }
}
