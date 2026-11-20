using System.Text.Json.Serialization;

namespace TrustNTE.Models.Dps
{
    /// <summary>battle.get_summary / event.battle.summary 的业务 DTO。</summary>
    public sealed class DpsBattleSummaryDto
    {
        [JsonPropertyName("duration_seconds")]
        public double DurationSeconds { get; set; }

        [JsonPropertyName("dps_time_mode")]
        public string DpsTimeMode { get; set; } = "";

        [JsonPropertyName("total_damage")]
        public double TotalDamage { get; set; }

        [JsonPropertyName("max_hp_reduction")]
        public double MaxHpReduction { get; set; }

        [JsonPropertyName("total_dps")]
        public double TotalDps { get; set; }

        [JsonPropertyName("total_damage_taken")]
        public double TotalDamageTaken { get; set; }

        [JsonPropertyName("total_hits")]
        public ulong TotalHits { get; set; }

        [JsonPropertyName("characters")]
        public List<CharacterInfo> Characters { get; set; } = new();

        [JsonPropertyName("skills")]
        public List<SkillInfo> Skills { get; set; } = new();

        [JsonPropertyName("abyss")]
        public AbyssInfo Abyss { get; set; } = new();

        [JsonPropertyName("quality")]
        public QualityInfo Quality { get; set; } = new();

        public sealed class CharacterInfo
        {
            [JsonPropertyName("char_id")]
            public uint CharId { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("hits")]
            public ulong Hits { get; set; }

            [JsonPropertyName("damage")]
            public double Damage { get; set; }

            [JsonPropertyName("dps")]
            public double Dps { get; set; }

            [JsonPropertyName("damage_share_percent")]
            public double DamageSharePercent { get; set; }

            [JsonPropertyName("hits_taken")]
            public ulong HitsTaken { get; set; }

            [JsonPropertyName("damage_taken")]
            public double DamageTaken { get; set; }
        }

        public sealed class SkillInfo
        {
            [JsonPropertyName("char_id")]
            public uint CharId { get; set; }

            [JsonPropertyName("char_name")]
            public string CharName { get; set; } = "";

            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("category")]
            public string Category { get; set; } = "";

            [JsonPropertyName("ability_name")]
            public string? AbilityName { get; set; }

            [JsonPropertyName("gameplay_effect_name")]
            public string? GameplayEffectName { get; set; }

            [JsonPropertyName("hits")]
            public ulong Hits { get; set; }

            [JsonPropertyName("damage")]
            public double Damage { get; set; }

            [JsonPropertyName("damage_share_percent")]
            public double DamageSharePercent { get; set; }

            [JsonPropertyName("is_follow_up")]
            public bool IsFollowUp { get; set; }
        }

        public sealed class AbyssInfo
        {
            [JsonPropertyName("detected")]
            public bool Detected { get; set; }

            [JsonPropertyName("floor")]
            public uint? Floor { get; set; }

            [JsonPropertyName("active_half")]
            public string? ActiveHalf { get; set; }

            [JsonPropertyName("success")]
            public bool Success { get; set; }

            [JsonPropertyName("first_half")]
            public AbyssHalfInfo? FirstHalf { get; set; }

            [JsonPropertyName("second_half")]
            public AbyssHalfInfo? SecondHalf { get; set; }
        }

        public sealed class AbyssHalfInfo
        {
            [JsonPropertyName("half")]
            public string Half { get; set; } = "";

            [JsonPropertyName("duration_seconds")]
            public double DurationSeconds { get; set; }

            [JsonPropertyName("total_damage")]
            public double TotalDamage { get; set; }

            [JsonPropertyName("total_dps")]
            public double TotalDps { get; set; }

            [JsonPropertyName("characters")]
            public List<CharacterInfo> Characters { get; set; } = new();

            [JsonPropertyName("skills")]
            public List<SkillInfo> Skills { get; set; } = new();
        }

        public sealed class QualityInfo
        {
            [JsonPropertyName("source")] public string Source { get; set; } = "";
            [JsonPropertyName("packet_count")] public ulong PacketCount { get; set; }
            [JsonPropertyName("packets_with_hits")] public ulong PacketsWithHits { get; set; }
            [JsonPropertyName("hit_count")] public ulong HitCount { get; set; }
            [JsonPropertyName("outgoing_hits")] public ulong OutgoingHits { get; set; }
            [JsonPropertyName("outgoing_damage")] public double OutgoingDamage { get; set; }
            [JsonPropertyName("unknown_direction_hits")] public ulong UnknownDirectionHits { get; set; }
            [JsonPropertyName("unknown_direction_damage")] public double UnknownDirectionDamage { get; set; }
            [JsonPropertyName("incoming_hits")] public ulong IncomingHits { get; set; }
            [JsonPropertyName("incoming_damage")] public double IncomingDamage { get; set; }
            [JsonPropertyName("unknown_character_count")] public ulong UnknownCharacterCount { get; set; }
            [JsonPropertyName("unknown_character_hits")] public ulong UnknownCharacterHits { get; set; }
            [JsonPropertyName("unmapped_skill_rows")] public ulong UnmappedSkillRows { get; set; }
            [JsonPropertyName("unmapped_skill_hits")] public ulong UnmappedSkillHits { get; set; }
            [JsonPropertyName("unmapped_gameplay_effect_count")] public ulong UnmappedGameplayEffectCount { get; set; }
            [JsonPropertyName("time_stop_event_count")] public ulong TimeStopEventCount { get; set; }
            [JsonPropertyName("time_stop_interval_count")] public ulong TimeStopIntervalCount { get; set; }
            [JsonPropertyName("abyss_event_count")] public ulong AbyssEventCount { get; set; }
            [JsonPropertyName("server_damage_corrections")] public ulong ServerDamageCorrections { get; set; }
            [JsonPropertyName("unattributed_server_damage_events")] public ulong UnattributedServerDamageEvents { get; set; }
            [JsonPropertyName("unattributed_server_damage")] public double UnattributedServerDamage { get; set; }
        }
    }
}
