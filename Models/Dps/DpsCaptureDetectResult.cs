using System.Text.Json.Serialization;

namespace TrustNTE.Models.Dps
{
    /// <summary>capture.detect 的真实返回快照；网卡 Name 必须原样回传给 Core。</summary>
    public sealed class DpsCaptureDetectResult
    {
        [JsonPropertyName("game_process_detected")]
        public bool GameProcessDetected { get; set; }

        [JsonPropertyName("recommended_device")]
        public string? RecommendedDevice { get; set; }

        [JsonPropertyName("local_ip_detected")]
        public bool LocalIpDetected { get; set; }

        [JsonPropertyName("devices")]
        public List<DeviceInfo> Devices { get; set; } = new();

        public sealed class DeviceInfo
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("description")]
            public string Description { get; set; } = "";

            [JsonPropertyName("ipv4")]
            public List<string> Ipv4 { get; set; } = new();
        }
    }
}
