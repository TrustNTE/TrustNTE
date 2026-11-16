using System;
using System.Collections.Generic;
using System.Linq;

namespace TrustNTE.Helpers
{
    public static class PickupListEvaluator
    {
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var buf = new char[s.Length];
            int n = 0;
            foreach (char c in s)
            {
                char m = c switch
                {
                    '\u3000' => ' ',
                    >= '\uFF01' and <= '\uFF5E' => (char)(c - 0xFEE0),
                    _ => c
                };
                buf[n++] = char.ToLowerInvariant(m);
            }
            return new string(buf, 0, n).Trim();
        }

        // ★ string? ：配置字段可能为 null，消 CS8604
        public static List<string> ParseLines(string? multiLineText)
            => (multiLineText ?? string.Empty)
               .Split(new[] { '\r', '\n', '，', ',', '、' }, StringSplitOptions.RemoveEmptyEntries)
               .Select(Normalize)
               .Where(l => l.Length > 0)
               .Distinct()
               .ToList();

        public static bool ShouldPick(string ocrText, bool blacklistMode,
            IEnumerable<string> whitelist, IEnumerable<string> blacklist)
        {
            string t = Normalize(ocrText);
            if (t.Length == 0) return false;

            if (blacklistMode)
            {
                var black = (blacklist ?? Array.Empty<string>())
                    .Select(Normalize).Where(s => s.Length > 0);
                return !black.Any(b => t.Contains(b));
            }
            else
            {
                var white = (whitelist ?? Array.Empty<string>())
                    .Select(Normalize).Where(s => s.Length > 0).ToList();
                if (white.Count == 0) return false;
                return white.Any(w => t.Contains(w));
            }
        }
    }
}
