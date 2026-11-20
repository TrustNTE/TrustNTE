using System.Collections.Generic;

namespace TrustNTE.Models
{
    public sealed class StrategyDocument
    {
        public int Schema { get; set; } = 1;
        public StrategyInfo Info { get; set; } = new();
        public List<string?> Team { get; set; } = new() { null, null, null, null };   // 0~3 = 1~4号位
        public List<StrategyAction> Actions { get; set; } = new();
    }
    public sealed class StrategyInfo
    {
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public string Description { get; set; } = "";
        public Dictionary<string, object>? Config { get; set; }
        public List<object>? PreActions { get; set; }
    }
    public sealed class StrategyAction
    {
        public string Name { get; set; } = "";
        public string Character { get; set; } = "";
        public string Action { get; set; } = "";
        public Dictionary<string, object>? Condition { get; set; }
        public bool EnsureCast { get; set; }
        public int Index { get; set; }
    }
}
