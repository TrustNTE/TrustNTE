using System.Text.Json.Serialization;

namespace TrustNTE.Models.Dps
{
    /// <summary>event.capture.status 的稳定协议字段。</summary>
    public sealed class DpsCaptureStatusDto
    {
        [JsonPropertyName("sequence")]
        public ulong Sequence { get; set; }

        [JsonPropertyName("operation_id")]
        public string OperationId { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("profile")]
        public string Profile { get; set; } = "";
    }
}
