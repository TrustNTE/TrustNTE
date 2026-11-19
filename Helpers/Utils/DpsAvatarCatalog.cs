using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TrustNTE.Helpers.Utils
{
    /// <summary>
    /// DPS 角色头像目录:char_id → 头像 png 绝对路径(独立模块,与 CharacterCatalog 解耦)。
    /// 数据源:Assets\AvatarDataTable\AvatarDataTable.json(游戏本体拆包头像表)。
    /// 键=纯数字 char_id(角色头像);Fashion_/Like_/1/2(时装/羁遇/默认头像)自动排除。
    /// AssetPathName 尾段 player_XXX_256_1 ↔ 256 目录 player_XXX_256.png(先原名,再去 _1 兜底)。
    /// 查不到返回 null,调用方用属性色首字圆环兜底。
    /// </summary>
    public static class DpsAvatarCatalog
    {
        private static readonly Lazy<Dictionary<string, string>> Map = new(Load);

        /// <summary>解析角色头像绝对路径;无表/无文件/非角色 ID 返回 null。</summary>
        public static string? Resolve(uint charId)
            => Map.Value.TryGetValue(charId.ToString(), out var p) ? p : null;

        private static Dictionary<string, string> Load()
        {
            var dict = new Dictionary<string, string>();
            try
            {
                var jsonPath = Path.Combine(AppContext.BaseDirectory,
                    "Assets", "AvatarDataTable", "AvatarDataTable.json");
                var pngDir = Path.Combine(AppContext.BaseDirectory,
                    "Assets", "AvatarDataTable", "256");
                if (!File.Exists(jsonPath)) return dict;

                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var rows = doc.RootElement.TryGetProperty("rows", out var rowsEl) ? rowsEl : doc.RootElement;
                foreach (var prop in rows.EnumerateObject())
                {
                    if (!uint.TryParse(prop.Name, out var charId) || charId < 1003)
                        continue; // 排除 Fashion_/Like_ 与默认头像(1/2),角色 ID 均 ≥1003

                    var asset = prop.Value.GetProperty("BigImage")
                                          .GetProperty("AssetPathName").GetString();
                    if (string.IsNullOrEmpty(asset) || asset == "None") continue;

                    // "/Game/UI/UI_Icon/AvatarImage/.../player_kaesi1_256_1.player_kaesi1_256_1" → "player_kaesi1_256_1"
                    var stem = asset.Split('/')[^1].Split('.')[0];
                    foreach (var candidate in new[] { stem, stem.EndsWith("_1") ? stem[..^2] : stem })
                    {
                        var path = Path.Combine(pngDir, candidate + ".png");
                        if (File.Exists(path)) { dict[charId.ToString()] = path; break; }
                    }
                }
            }
            catch { /* 表损坏或缺文件时静默降级为首字圆环兜底 */ }
            return dict;
        }
    }
}
