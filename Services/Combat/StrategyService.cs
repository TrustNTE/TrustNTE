using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using TrustNTE.Models;

namespace TrustNTE.Services
{
    /// <summary>战斗策略文件读写：UserData\Strategies 下{策略名}.json，schema v1。</summary>
    public static class StrategyService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // 中文不转 \uXXXX
        };

        public static string Dir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "Strategies");

        public static IReadOnlyList<string> ListNames() =>
            Directory.Exists(Dir)
                ? Directory.GetFiles(Dir, "*.json")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrWhiteSpace(n))   // ★ 空名过滤（顺带挡占位垃圾文件名）
                    .Select(n => n!)                             // ★ 空性豁免，消 CS8619
                    .OrderBy(n => n).ToList()
                : new List<string>();

        public static StrategyDocument? Load(string name)
        {
            var path = Path.Combine(Dir, Sanitize(name) + ".json");
            if (!File.Exists(path)) return null;
            try
            {
                var doc = JsonSerializer.Deserialize<StrategyDocument>(File.ReadAllText(path));
                if (doc == null || doc.Schema != 1) return null;   // schema 校验，非 v1 拒载
                return doc;
            }
            catch { return null; }
        }

        public static void Save(StrategyDocument doc)
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, Sanitize(doc.Info.Name) + ".json");
            // 原子写：先落临时文件再 Move 替换，断电/崩溃不会留下半截 JSON
            var tmp = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts));
            File.Move(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { /* Move 成功后 tmp 已不存在 */ }
        }

        public static void ExportTo(StrategyDocument doc, string path) =>
            File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOpts));

        public static void Delete(string name)
        {
            var p = Path.Combine(Dir, Sanitize(name) + ".json");
            if (File.Exists(p)) File.Delete(p);
        }

        /// <summary>导入：宽松校验，警告不阻断（分享生态宁可宽松）。警告走 UI 状态行展示。</summary>
        public static (StrategyDocument? Doc, List<string> Warnings) ImportFrom(string path)
        {
            var warnings = new List<string>();
            try
            {
                var doc = JsonSerializer.Deserialize<StrategyDocument>(File.ReadAllText(path));
                if (doc == null) return (null, new List<string> { "JSON 解析失败" });
                if (doc.Schema != 1) warnings.Add($"schema={doc.Schema}，非 v1 格式，可能不兼容");
                if (doc.Team.Count > 4) { doc.Team = doc.Team.Take(4).ToList(); warnings.Add("队伍超过4人，已截断"); }

                var team = doc.Team.Where(t => !string.IsNullOrWhiteSpace(t)).ToHashSet();
                foreach (var a in doc.Actions)
                {
                    if (!string.IsNullOrEmpty(a.Character) && !team.Contains(a.Character))
                        warnings.Add($"动作[{a.Name}] 引用角色「{a.Character}」不在队伍中");
                    if (a.Action.Contains("moveby") || a.Action.Contains("scroll"))
                        warnings.Add($"动作[{a.Name}] 含视角操作(moveby/scroll)，效果依赖玩家灵敏度");
                }
                return (doc, warnings);
            }
            catch (Exception ex) { return (null, new List<string> { ex.Message }); }
        }

        private static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
            return string.IsNullOrEmpty(clean) ? "未命名策略" : clean;
        }
    }
}
