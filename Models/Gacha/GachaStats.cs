using CommunityToolkit.Mvvm.ComponentModel;

namespace TrustNTE.Models.Gacha
{
    /// <summary>
    /// 卡池汇总（对标参考图：总抽数/出卡数/S级平均）。
    /// HardPity 为游戏保底常量（交接规格 §9.2：Monopoly 90 / Fork 60），Phase 5 规则库统一管理。
    /// S级平均 = 总抽数 ÷ 出卡数（参考图可验算：491÷8=61.4）。
    /// </summary>
    public partial class PoolSummary : ObservableObject
    {
        public string Kind { get; init; } = "";       // limited / standard / arc
        public string Name { get; init; } = "";       // 限定卡池 / 常驻卡池 / 弧盘池
        public int HardPity { get; init; }
        [ObservableProperty] private bool _isSelected;
        [ObservableProperty] private int _totalPulls;
        [ObservableProperty] private int _fiveStars;
        [ObservableProperty] private string _avgPerFive = "0.0";
    }


}
