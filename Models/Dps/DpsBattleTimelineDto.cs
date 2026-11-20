using System.Text.Json.Serialization;

namespace TrustNTE.Models.Dps
{
    /// <summary>battle.get_timeline 的界面所需投影；未使用字段由 JSON 反序列化器忽略。</summary>
    public sealed class DpsBattleTimelineDto
    {
        [JsonPropertyName("battle_record_id")]
        public string BattleRecordId { get; set; } = "";

        [JsonPropertyName("generation")]
        public string Generation { get; set; } = "";

        [JsonPropertyName("finalized")]
        public bool Finalized { get; set; }

        [JsonPropertyName("complete")]
        public bool Complete { get; set; }

        [JsonPropertyName("effective_bucket_seconds")]
        public double EffectiveBucketSeconds { get; set; }

        [JsonPropertyName("duration_seconds")]
        public double DurationSeconds { get; set; }

        [JsonPropertyName("total_damage")]
        public double TotalDamage { get; set; }

        [JsonPropertyName("peak_dps")]
        public double PeakDps { get; set; }

        [JsonPropertyName("time_stop_intervals")]
        public List<IntervalInfo> TimeStopIntervals { get; set; } = new();

        [JsonPropertyName("markers")]
        public List<MarkerInfo> Markers { get; set; } = new();

        [JsonPropertyName("buckets")]
        public List<BucketInfo> Buckets { get; set; } = new();

        public sealed class IntervalInfo
        {
            [JsonPropertyName("start_offset_seconds")]
            public double StartOffsetSeconds { get; set; }

            [JsonPropertyName("end_offset_seconds")]
            public double EndOffsetSeconds { get; set; }

            [JsonPropertyName("pause_type_mask")]
            public uint? PauseTypeMask { get; set; }
        }

        public sealed class MarkerInfo
        {
            [JsonPropertyName("offset_seconds")]
            public double OffsetSeconds { get; set; }

            [JsonPropertyName("label_key")]
            public string LabelKey { get; set; } = "";

            [JsonPropertyName("kind")]
            public string Kind { get; set; } = "";
        }

        public sealed class BucketInfo
        {
            [JsonPropertyName("start_offset_seconds")]
            public double StartOffsetSeconds { get; set; }

            [JsonPropertyName("end_offset_seconds")]
            public double EndOffsetSeconds { get; set; }

            [JsonPropertyName("team_dps")]
            public double TeamDps { get; set; }

            [JsonPropertyName("damage")]
            public double Damage { get; set; }

            [JsonPropertyName("hits")]
            public string Hits { get; set; } = "0";

            [JsonPropertyName("cumulative_damage")]
            public double CumulativeDamage { get; set; }
        }
    }
}
