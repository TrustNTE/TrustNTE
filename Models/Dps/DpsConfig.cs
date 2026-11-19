using CommunityToolkit.Mvvm.ComponentModel;

namespace TrustNTE.Models.Dps
{
    /// <summary>DPS 抓包配置(ConfigService 自动落盘)。对齐原项目:默认自动选网卡。</summary>
    public sealed partial class DpsConfig : ObservableObject
    {
        /// <summary>风险页选中的网卡显示名;空 = 自动模式(核心按 HTGame.exe 活动连接自选网卡)。</summary>
        [ObservableProperty] private string _nicDisplayName = "";
    }
}
