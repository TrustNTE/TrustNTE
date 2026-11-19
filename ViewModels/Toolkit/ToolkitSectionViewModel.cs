using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TrustNTE.Helpers;
using TrustNTE.Models.Toolkit;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.ViewModels.Toolkit
{
    /// <summary>侧边栏「工具箱」段 VM:只提供数据与命令。语言切换广播自动刷新显示名。</summary>
    public partial class ToolkitSectionViewModel : ObservableObject, IRecipient<LanguageChangedMessage>
    {
        private readonly IToolkitService _toolkitService;

        public ObservableCollection<ToolkitToolItem> Tools { get; }

        [ObservableProperty] private string _groupTitle = "";

        public ToolkitSectionViewModel(IToolkitService toolkitService)
        {
            _toolkitService = toolkitService;
            Tools = new ObservableCollection<ToolkitToolItem>();
            foreach (var d in _toolkitService.GetTools())
                Tools.Add(new ToolkitToolItem(d));

            WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this);
            RefreshTexts();
        }

        public void Receive(LanguageChangedMessage message) => RefreshTexts();

        private void RefreshTexts()
        {
            GroupTitle = TranslationService.Instance["工具箱"];
            foreach (var item in Tools)
                item.DisplayName = TranslationService.Instance[item.Descriptor.DisplayName];
        }

        [RelayCommand]
        private void ActivateTool(ToolkitToolItem? item)
        {
            if (item == null || !item.Descriptor.IsEnabled) return;
            _toolkitService.Activate(item.Descriptor);
        }
    }

    /// <summary>侧边栏单条工具的可观察包装(DisplayName 变更驱动菜单项刷新)。</summary>
    public partial class ToolkitToolItem : ObservableObject
    {
        public ToolkitToolDescriptor Descriptor { get; }

        [ObservableProperty] private string _displayName;

        public ToolkitToolItem(ToolkitToolDescriptor descriptor)
        {
            Descriptor = descriptor;
            _displayName = descriptor.DisplayName;
        }
    }
}
