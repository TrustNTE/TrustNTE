using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TrustNTE.Services.Dps
{
    /// <summary>
    /// 演示种子数据加载(Assets\Nte_Dps\DemoSeed.json):
    /// 角色阵容/技能不再硬编码在 VM;文件缺失或坏档 → 内置默认,演示模式永不中断。
    /// </summary>
    public static class DemoSeedStore
    {
        public sealed record DemoChar(uint Id, string Name, string Attribute, double Value);
        public sealed record DemoSkill(string Char, string Skill, string Attribute, bool FollowUp, double Value);
        public sealed record DemoSeed(IReadOnlyList<DemoChar> Characters, IReadOnlyList<DemoSkill> Skills);

        private const string RelPath = "Assets/Nte_Dps/DemoSeed.json";
        private static DemoSeed? _cache;
        private static readonly object _lock = new();

        public static DemoSeed Load()
        {
            lock (_lock)
            {
                if (_cache is not null) return _cache;
                _cache = TryLoadFile() ?? BuiltIn();
                return _cache;
            }
        }

        private static DemoSeed? TryLoadFile()
        {
            var path = Path.Combine(AppContext.BaseDirectory, RelPath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(path)) return null;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                var chars = new List<DemoChar>();
                foreach (var e in root.GetProperty("characters").EnumerateArray())
                    chars.Add(new DemoChar(
                        e.GetProperty("id").GetUInt32(),
                        e.GetProperty("name").GetString() ?? "",
                        e.GetProperty("attribute").GetString() ?? "",
                        e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0));

                var skills = new List<DemoSkill>();
                foreach (var e in root.GetProperty("skills").EnumerateArray())
                    skills.Add(new DemoSkill(
                        e.GetProperty("char").GetString() ?? "",
                        e.GetProperty("skill").GetString() ?? "",
                        e.GetProperty("attribute").GetString() ?? "",
                        e.TryGetProperty("followUp", out var f) && f.ValueKind == JsonValueKind.True,
                        e.TryGetProperty("value", out var v2) && v2.ValueKind == JsonValueKind.Number ? v2.GetDouble() : 0));

                return chars.Count == 0 ? null : new DemoSeed(chars, skills);
            }
            catch { return null; }   // 坏档回退内置,不阻断演示模式
        }

        private static DemoSeed BuiltIn() => new(
            new List<DemoChar>
            {
                new(1071, "卡厄斯", "相", 8842310.0),
                new(1010, "娜娜莉", "灵", 4848130.0),
                new(1076, "真红",   "光", 3049630.0),
                new(1004, "安魂曲", "暗", 1915790.0),
            },
            new List<DemoSkill>
            {
                new("卡厄斯", "湮灭回响", "相", false, 6333850.0),
                new("卡厄斯", "终焉序列", "相", true,  2502260.0),
                new("娜娜莉", "潮汐裂斩", "灵", false, 2189480.0),
                new("真红",   "炽阳宣告", "光", false, 1876700.0),
                new("娜娜莉", "深海回涌", "灵", true,  1642110.0),
                new("安魂曲", "安魂礼拜", "暗", false, 1212030.0),
                new("真红",   "裁决之炎", "光", true,  860150.0),
            });
    }
}
