using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TrustNTE.Services.Dps
{
    /// <summary>
    /// 角色资料表(Assets\Nte_Dps\characters.json,格式与原项目 res/data/characters/characters.json 一致):
    /// 角色ID → 中文名/属性。新增角色只改 JSON,不改代码。
    /// 头像解析统一走既有轮子 DpsAvatarCatalog(本表不掺和,避免双头像体系)。
    /// </summary>
    public static class DpsCharacterTable
    {
        private const string RelPath = "Assets/Nte_Dps/characters.json";
        private static Dictionary<uint, (string Name, string Attr)>? _table;
        private static readonly object _lock = new();

        /// <summary>角色中文名;查不到返回 null(调用方回退 "#ID")。</summary>
        public static string? GetName(uint id) => TryGet(id, out var v) ? v.Name : null;

        /// <summary>角色属性(相/灵/光/暗/咒/魂);查不到返回 null(调用方回退)。</summary>
        public static string? GetAttribute(uint id) => TryGet(id, out var v) ? v.Attr : null;

        /// <summary>已加载角色条数(自检用)。</summary>
        public static int Count => GetTable()?.Count ?? 0;

        private static bool TryGet(uint id, out (string Name, string Attr) val)
        {
            var t = GetTable();
            if (t is not null && t.TryGetValue(id, out var found)) { val = found; return true; }
            val = default; return false;
        }

        private static Dictionary<uint, (string, string)>? GetTable()
        {
            lock (_lock)
            {
                if (_table is not null) return _table;
                _table = TryLoad();
                return _table;
            }
        }

        private static Dictionary<uint, (string, string)>? TryLoad()
        {
            var path = Path.Combine(AppContext.BaseDirectory, RelPath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(path)) return null;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var characters = doc.RootElement.GetProperty("characters");
                var table = new Dictionary<uint, (string, string)>();

                foreach (var prop in characters.EnumerateObject())
                {
                    if (!uint.TryParse(prop.Name, out var id)) continue;   // 过滤非数字键
                    var e = prop.Value;
                    var name = e.TryGetProperty("name_zh", out var nz) && nz.ValueKind == JsonValueKind.String
                               ? nz.GetString()!
                               : e.TryGetProperty("name_en", out var ne) ? ne.GetString() ?? "" : "";
                    var attr = e.TryGetProperty("attribute", out var at) && at.ValueKind == JsonValueKind.String
                               ? at.GetString() ?? "" : "";
                    table[id] = (name, attr);
                }
                return table.Count == 0 ? null : table;
            }
            catch { return null; }   // 坏档 → null,调用方回退,不阻断
        }
    }
}
