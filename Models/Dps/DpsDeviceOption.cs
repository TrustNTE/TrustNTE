namespace TrustNTE.Models.Dps
{
    /// <summary>DPS 风险页网卡选项；自动模式的 Name 为 null。</summary>
    public sealed class DpsDeviceOption
    {
        public string? Name { get; }
        public string DisplayName { get; }

        public DpsDeviceOption(string? name, string displayName)
        {
            Name = name;
            DisplayName = displayName;
        }
    }
}
