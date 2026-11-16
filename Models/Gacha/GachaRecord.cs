using System;

namespace TrustNTE.Models.Gacha
{
    /// <summary>单条抽卡记录（UI 绑定模型）。字段对齐公开 JSON v2.1。</summary>
    public sealed class GachaRecord
    {
        public DateTime Time { get; init; }
        public string RecordType { get; init; } = "";   // monopoly / fork
        public string RecordId { get; init; } = "";     // 语义键稳定哈希（Phase 3 生成）
        public string PoolId { get; init; } = "";
        public string PoolName { get; init; } = "";     // Phase 5 名称地图填充，先裸 ID
        public string LotteryId { get; init; } = "";    // monopoly 专用；Lottery_Permanent → 常驻池判定（规格 §9.3）
        public string ItemId { get; init; } = "";
        public string ItemName { get; init; } = "";
        public int Rarity { get; init; }                // 来自 core.json 物品地图
        public int Count { get; init; } = 1;
    }
}
