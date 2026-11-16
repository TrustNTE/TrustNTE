namespace TrustNTE.Services.Interfaces
{
    /// <summary>DPS 统计独立窗口入口。独立于工具箱模块,由 DPS 业务域自持。</summary>
    public interface IDpsWindowService
    {
        /// <summary>打开 DPS 统计窗口(防重复;已开则激活既有窗口)。</summary>
        void ShowDpsAnalyzer();
    }
}
