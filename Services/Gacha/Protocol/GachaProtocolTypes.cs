using System;
using System.Text;

namespace TrustNTE.Services.Gacha.Protocol
{
    /// <summary>
    /// 协议指纹常量与校验（nte-capture protocol/* + nte-core public_json.rs / rules/resolution.rs 原文移植）。
    /// marker 可能以位偏移形式出现在载荷中 → 引擎按 raw + shift8:1..7 双视图扫描。
    /// 常量是协议身份证，禁止放进用户配置（红线5 边界）。
    /// </summary>
    public static class GachaProtocolTypes
    {
        // ── marker（types.rs/envelope.rs 原文）──
        public const string MonopolyMarker = "FMonopolyLotteryRecordData";
        public const string ForkMarker = "FForkLotteryRecordData";

        public static readonly byte[] MonopolyMarkerBytes = Encoding.ASCII.GetBytes(MonopolyMarker);
        public static readonly byte[] ForkMarkerBytes = Encoding.ASCII.GetBytes(ForkMarker);

        // ── 信封常量（envelope.rs/types.rs 原文）──
        public const uint ProtocolConstant = 0x03000000;
        public const uint MonopolyBlockKind = 527;
        public const uint ForkBlockKind = 5906;
        public const uint MonopolyFooter = 1_774_080;
        public const uint MaxRowsPerBlock = 100;

        /// <summary>位偏移视图起始字节偏移（parse_payload_blocks 固定值 8）。</summary>
        public const int ShiftViewByteOffset = 8;

        // ── 公开导出契约（nte-core public_json.rs 原文）──
        public const string PublicJsonSchema = "nte-gacha-export";
        public const string PublicJsonSchemaVersion = "2.1";

        public const long RollPointSentinelZero = 0;             // 点滴赠礼
        public const long RollPointSentinelMax = 4_294_967_295; // 沉眠地

        public static bool IsRollPointSentinel(long value) => value is 0 or 4_294_967_295;

        /// <summary>哨兵 → roll_label_id（roll_label_id_from_sentinel 原文移植；非哨兵返回 null）。</summary>
        public static string? RollLabelFromSentinel(long value) => value switch
        {
            0 => "BPUI_LotteryResult_jidianzengli",
            4_294_967_295 => "BPUI_LotteryResult_chenmiandi",
            _ => null,
        };

        /// <summary>
        /// lottery_id 校验（is_valid_lottery_id 原文移植）：
        /// Lottery_ 前缀 + 尾部非空 + 尾部仅 ASCII 字母/数字/下划线。
        /// </summary>
        public static bool IsValidLotteryId(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith("Lottery_", StringComparison.Ordinal)) return false;
            var tail = value.Substring("Lottery_".Length);
            if (tail.Length == 0) return false;
            foreach (var c in tail)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>池归类（resolution.rs classify_pool_id 原文移植）；未知 pool_id → null（原文 UnknownPoolId）。</summary>
        public static string? ClassifyPoolKind(string poolId) => poolId switch
        {
            "CardPool_Character" => "monopoly_limited",
            "CardPool_NewRole" => "monopoly_standard",
            var s when s.StartsWith("ForkLottery_", StringComparison.Ordinal) => "fork_lottery",
            _ => null,
        };

        /// <summary>record_type 一致性（public_json.rs parse_record 原文移植）。</summary>
        public static string? ExpectedRecordType(string poolKind) => poolKind switch
        {
            "monopoly_limited" or "monopoly_standard" => "monopoly",
            "fork_lottery" => "fork",
            _ => null,
        };
    }
}
