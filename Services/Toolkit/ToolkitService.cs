using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TrustNTE.Models.Toolkit;
using TrustNTE.Services.Interfaces;
using TrustNTE.ViewModels.Pages;
using TrustNTE.Views.Windows;

namespace TrustNTE.Services.Toolkit
{
    /// <summary>
    /// 工具箱注册表。抽卡分析：弹出独立窗口 GachaAnalyzerWindow（不进主窗口页面区，防重复打开）。
    /// DPS 统计待后续上线再加激活逻辑。
    /// </summary>
    public class ToolkitService : IToolkitService
    {
        private readonly DevToolsWindowService _devTools;
        private readonly GachaAnalysisPageViewModel _gachaVm;

        private readonly List<ToolkitToolDescriptor> _tools = new()
        {
            new() { Key="devtools", DisplayName="开发者工具", IconName="Bug24", IsEnabled=true, Kind=ToolkitToolKind.DevTools },
            new() { Key="dps", DisplayName="DPS 统计", IconName="ChartMultiple24", IsEnabled=true, Kind=ToolkitToolKind.Placeholder },
            new() { Key="gacha", DisplayName="抽卡分析", IconName="Sparkle24", IsEnabled=true, Kind=ToolkitToolKind.GachaAnalysis },
        };

        private readonly IDpsWindowService _dpsWindows;   // 字段区加这行

        public ToolkitService(DevToolsWindowService devTools, GachaAnalysisPageViewModel gachaVm, IDpsWindowService dpsWindows)
        {
            _devTools = devTools;
            _gachaVm = gachaVm;
            _dpsWindows = dpsWindows;
        }

        public IReadOnlyList<ToolkitToolDescriptor> GetTools() => _tools;

        public void Activate(ToolkitToolDescriptor tool)
        {
            if (tool == null || !tool.IsEnabled) return;
            switch (tool.Kind)
            {
                case ToolkitToolKind.DevTools:
                    _devTools.ShowDevTools();
                    break;
                case ToolkitToolKind.GachaAnalysis:
                    ShowGachaAnalyzer();
                    break;
                case ToolkitToolKind.Placeholder when tool.Key == "dps":
                    _dpsWindows.ShowDpsAnalyzer();
                    break;
            }
        }

        private void ShowGachaAnalyzer()
        {
            var existing = Application.Current?.Windows.OfType<GachaAnalyzerWindow>().FirstOrDefault();
            if (existing != null) { existing.Activate(); return; } // 防重复打开

            var win = new GachaAnalyzerWindow(_gachaVm);
            if (Application.Current?.MainWindow is { IsLoaded: true } owner)
                win.Owner = owner;
            win.Show();
        }
    }
}
