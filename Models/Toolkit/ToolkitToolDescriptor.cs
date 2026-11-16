namespace TrustNTE.Models.Toolkit
{
    /// <summary>工具种类。DPS 统计待 Phase 后续上线再加激活逻辑。</summary>
    public enum ToolkitToolKind
    {
        Placeholder,   // 灰置占位
        DevTools,      // 开发者工具(取色器)
        GachaAnalysis, // 抽卡分析(导航至主窗口页面)
    }

    /// <summary>工具箱条目(纯数据,零 UI 依赖)。显示名同时充当翻译 key。</summary>
    public sealed class ToolkitToolDescriptor
    {
        public string Key { get; init; } = "";
        public string DisplayName { get; init; } = "";
        /// <summary>SymbolRegular 枚举名,解析失败回落 Wrench24。</summary>
        public string IconName { get; init; } = "";
        public bool IsEnabled { get; init; } = true;
        public ToolkitToolKind Kind { get; init; }
    }
}
