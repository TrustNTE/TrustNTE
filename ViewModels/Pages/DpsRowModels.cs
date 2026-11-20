using CommunityToolkit.Mvvm.ComponentModel;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Geometry = System.Windows.Media.Geometry;

namespace TrustNTE.ViewModels.Pages
{
    /// <summary>DPS 页行模型；数据来自 Core 战斗摘要、时间线和逐击分页。</summary>
    public partial class CharacterRowVm : ObservableObject
    {
        public uint CharId { get; }
        public string Name { get; }
        public string Attribute { get; }
        public string Initial { get; }
        public string? AvatarPath { get; }
        public bool HasAvatar => AvatarPath != null;
        public double Num { get; set; }
        [ObservableProperty] private string _damageStr = "0";
        [ObservableProperty] private string _dpsStr = "DPS 0";
        [ObservableProperty] private string _shareStr = "0%";
        [ObservableProperty] private double _barWidth;

        public CharacterRowVm(uint charId, string name, string attribute)
        {
            CharId = charId;
            Name = name;
            Attribute = attribute;
            Initial = name.Length > 0 ? name[..1] : "?";
            AvatarPath = TrustNTE.Helpers.Utils.DpsAvatarCatalog.Resolve(charId);
        }
    }

    public partial class SkillRowVm : ObservableObject
    {
        public string Key { get; }
        public uint CharId { get; }
        public string CharName { get; }
        public string SkillName { get; }
        public string Attribute { get; }
        public bool IsFollowUp { get; }
        public double Num { get; set; }
        [ObservableProperty] private string _damageStr = "0";
        [ObservableProperty] private string _pctStr = "0%";
        [ObservableProperty] private double _barWidth;

        public SkillRowVm(
            string key, uint charId, string charName, string skillName, string attribute, bool followUp)
        {
            Key = key;
            CharId = charId;
            CharName = charName;
            SkillName = skillName;
            Attribute = attribute;
            IsFollowUp = followUp;
        }
    }

    public partial class BucketBarVm : ObservableObject
    {
        public string Kind { get; init; } = "dps";
        public double Num { get; set; }
        [ObservableProperty] private double _heightPct = 8;
    }

    public class QualityChipVm
    {
        public string Label { get; }
        public string ValueStr { get; }
        public bool IsWarn { get; }

        public QualityChipVm(string label, string value, bool warn)
        {
            Label = label;
            ValueStr = value;
            IsWarn = warn;
        }
    }

    public class AxisRowVm
    {
        public string TimeStr { get; init; } = "";
        public string TypeStr { get; init; } = "";
        public string DamageStr { get; init; } = "";
        public string TargetStr { get; init; } = "";
    }

    public partial class DetailHeaderVm : ObservableObject
    {
        [ObservableProperty] private string _name = "";
        [ObservableProperty] private string _attribute = "";
        [ObservableProperty] private string _initial = "";
        [ObservableProperty] private string _totalStr = "0";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAvatar))]
        private string? _avatarPath;
        public bool HasAvatar => AvatarPath != null;
    }

    public partial class DonutSliceVm : ObservableObject
    {
        [ObservableProperty] private Geometry _geometry = Geometry.Empty;
        [ObservableProperty] private Brush _fillBrush = Brushes.DimGray;
        public string Label { get; init; } = "";
    }
}
