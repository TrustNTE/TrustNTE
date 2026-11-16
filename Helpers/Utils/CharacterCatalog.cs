using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TrustNTE.Helpers
{
    public sealed record CharacterRecord(string Name, string[] Rarities, string Element);

    /// <summary>
    /// 角色名册（数据驱动）：Assets\Characters\roster.json。
    /// 版本更新出新角色 → 改 json 不改代码不重编译。
    /// </summary>
    public static class CharacterCatalog
    {
        private static readonly Lazy<List<CharacterRecord>> _all = new(Load);
        public static IReadOnlyList<CharacterRecord> All => _all.Value;

        /// <summary>两维筛选；零的属性“全”通配任何属性筛选。</summary>
        public static IEnumerable<CharacterRecord> Filter(string rarity, string element) =>
            All.Where(c =>
                (rarity == "全部" || c.Rarities.Contains(rarity)) &&
                (element == "全部" || c.Element == element || c.Element == "全"));

        private static List<CharacterRecord> Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Characters", "roster.json");
            if (!File.Exists(path))
                throw new FileNotFoundException($"角色名册缺失: {path}（检查 csproj 的 Content 拷贝规则）");
            //   roster.json 用 camelCase("characters")，C# 属性是 PascalCase(Characters)，
            //   默认大小写敏感会静默匹配不上 → 空列表不报错——就是这个 bug 掏空了你的列表
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            var doc = JsonSerializer.Deserialize<RosterFile>(File.ReadAllText(path), opts)
                      ?? throw new InvalidOperationException("roster.json 解析失败");
            if (doc.Characters.Count == 0)
                throw new InvalidOperationException("roster.json 里 characters 为空（检查 json 格式）");
            return doc.Characters.Select(c => new CharacterRecord(c.Name, c.Rarities, c.Element)).ToList();
        }

        private sealed class RosterFile { public string Version { get; set; } = ""; public List<RosterEntry> Characters { get; set; } = new(); }
        private sealed class RosterEntry { public string Name { get; set; } = ""; public string[] Rarities { get; set; } = []; public string Element { get; set; } = ""; }
    }
}
