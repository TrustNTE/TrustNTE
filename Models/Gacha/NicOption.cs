namespace TrustNTE.Models.Gacha
{
    /// <summary>网卡下拉框选项模型。DeviceName 是 pcap 设备系统名（传给引擎），DisplayName 是界面显示名。</summary>
    public sealed class NicOption
    {
        public string DeviceName { get; init; } = "";
        public string DisplayName { get; init; } = "";

        public override string ToString() => DisplayName;
    }
}
