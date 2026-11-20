using System.Text.Json.Serialization;

namespace TrustNTE.Models.Dps
{
    /// <summary>battle.get_axis 的有界分页；sequence/cursor 保持字符串避免 64 位精度损失。</summary>
    public sealed class DpsBattleAxisPageDto
    {
        [JsonPropertyName("battle_record_id")]
        public string BattleRecordId { get; set; } = "";

        [JsonPropertyName("generation")]
        public string Generation { get; set; } = "0";

        [JsonPropertyName("finalized")]
        public bool Finalized { get; set; }

        [JsonPropertyName("complete")]
        public bool Complete { get; set; }

        [JsonPropertyName("next_cursor")]
        public string? NextCursor { get; set; }

        [JsonPropertyName("total_hits")]
        public string TotalHits { get; set; } = "0";

        [JsonPropertyName("rows")]
        public List<HitInfo> Rows { get; set; } = new();

        public sealed class HitInfo
        {
            [JsonPropertyName("sequence")]
            public string Sequence { get; set; } = "";

            [JsonPropertyName("relative_time_seconds")]
            public double RelativeTimeSeconds { get; set; }

            [JsonPropertyName("character_id")]
            public uint CharacterId { get; set; }

            [JsonPropertyName("character_name")]
            public string CharacterName { get; set; } = "";

            [JsonPropertyName("direction")]
            public string Direction { get; set; } = "";

            [JsonPropertyName("damage")]
            public double Damage { get; set; }

            [JsonPropertyName("follow_up_damage")]
            public double FollowUpDamage { get; set; }

            [JsonPropertyName("total_damage")]
            public double TotalDamage { get; set; }

            [JsonPropertyName("max_hp_reduction")]
            public double? MaxHpReduction { get; set; }

            [JsonPropertyName("target_id")]
            public string? TargetId { get; set; }

            [JsonPropertyName("target_name")]
            public string? TargetName { get; set; }

            [JsonPropertyName("gameplay_effect_name")]
            public string? GameplayEffectName { get; set; }

            [JsonPropertyName("ability_name")]
            public string? AbilityName { get; set; }

            [JsonPropertyName("damage_name")]
            public string? DamageName { get; set; }

            [JsonPropertyName("attack_type")]
            public string? AttackType { get; set; }

            [JsonPropertyName("damage_attribute")]
            public string? DamageAttribute { get; set; }
        }
    }
}
