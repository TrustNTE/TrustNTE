using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using Wpf.Ui.Controls;
using TrustNTE.ViewModels.Toolkit;

namespace TrustNTE.Helpers.Presentation
{
    /// <summary>
    /// 工具箱侧边栏映射器(纯视图职责):描述符 → NavigationViewItem。
    /// Content 用绑定挂到 VM 的可观察属性(语言切换自动跟随);点击只转发 VM 命令。
    /// </summary>
    public static class ToolkitMenuBinder
    {
        private const string GroupTag = "ToolkitGroup";

        public static void Bind(NavigationView nav, ToolkitSectionViewModel vm)
        {
            if (nav == null || vm == null) return;
            if (nav.MenuItems.OfType<NavigationViewItem>().Any(i => Equals(i.Tag, GroupTag)))
                return; // 防重复注入

            var group = new NavigationViewItem
            {
                Tag = GroupTag,
                Icon = new SymbolIcon { Symbol = ParseIcon("ToolBox24") }
            };
            group.SetBinding(ContentControl.ContentProperty,
                new Binding(nameof(ToolkitSectionViewModel.GroupTitle)) { Source = vm });

            foreach (var item in vm.Tools)
            {
                var child = new NavigationViewItem
                {
                    Tag = item.Descriptor.Key,
                    IsEnabled = item.Descriptor.IsEnabled,
                    Icon = new SymbolIcon { Symbol = ParseIcon(item.Descriptor.IconName) }
                };
                child.SetBinding(ContentControl.ContentProperty,
                    new Binding(nameof(ToolkitToolItem.DisplayName)) { Source = item });

                if (item.Descriptor.IsEnabled)
                {
                    var captured = item;
                    child.PreviewMouseLeftButtonUp += (_, _) => vm.ActivateToolCommand.Execute(captured);
                }
                group.MenuItems.Add(child);
            }

            // 顶层最后一项(快捷键)之前 —— 布局依据当前 MainWindow.xaml;若日后调序,改这一行
            var insertAt = Math.Max(0, nav.MenuItems.Count - 1);
            nav.MenuItems.Insert(insertAt, group);
        }

        private static SymbolRegular ParseIcon(string name) =>
            Enum.TryParse(name, out SymbolRegular symbol) ? symbol : SymbolRegular.Wrench24;
    }
}
