using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrustNTE.Services.Interfaces;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Geometry = System.Windows.Media.Geometry;


namespace TrustNTE.ViewModels.Pages
{
    /// <summary>
    /// DPS 统计页 VM(独立模块,与抽卡/工具箱零耦合):
    /// 风险层 / 采集中 / 角色详情 三视图状态机 + 内置 4Hz 模拟数据泵。
    /// 阶段二把泵替换为 DpsCoreService 真实事件流,绑定字段名保持不变。
    /// 日志:[DPS] 前缀,演示版仅 LogInfo;LogWarning/Error/Debug 在阶段二接 ILogService 完整签名。
    /// </summary>
    public partial class DpsAnalysisPageViewModel : ObservableObject
    {
        private readonly ILogService _logService;

        // ── 视图状态机(窗口内跳转) ──
        [ObservableProperty] private bool _isRiskView = true;
        [ObservableProperty] private bool _isLiveView;
        [ObservableProperty] private bool _isDetailView;


        // ── 时间线三模式 ──
        [ObservableProperty] private bool _isBarMode = true;
        [ObservableProperty] private bool _isLineMode;
        [ObservableProperty] private bool _isDonutMode;
        [ObservableProperty] private PointCollection _linePoints = new();
        [ObservableProperty] private PointCollection _areaPoints = new();
        public ObservableCollection<DonutSliceVm> DonutSlices { get; } = new();



        // ── 风险层 ──
        [ObservableProperty] private bool _acceptedRisk;
        [ObservableProperty] private string _selectedDevice = "全部网卡(默认)";
        public ObservableCollection<string> Devices { get; } = new();

        // ── KPI(字段名对应 BattleSummaryDto) ──
        [ObservableProperty] private string _totalDamageStr = "0";
        [ObservableProperty] private string _teamDpsStr = "0";
        [ObservableProperty] private string _durationStr = "00:00";
        [ObservableProperty] private string _damageTakenStr = "0";
        [ObservableProperty] private string _hitsStr = "0";
        [ObservableProperty] private string _maxHpReductionStr = "0";
        [ObservableProperty] private string _elapsedBadge = "00:00";

        // ── 状态灯 ──
        [ObservableProperty] private bool _coreLampOn = true;
        [ObservableProperty] private bool _gameLampOn = true;
        [ObservableProperty] private bool _abyssLampOn;

        // ── 榜单与曲线 ──
        public ObservableCollection<CharacterRowVm> Characters { get; } = new();
        public ObservableCollection<SkillRowVm> Skills { get; } = new();
        public ObservableCollection<BucketBarVm> TimelineBars { get; } = new();
        public ObservableCollection<QualityChipVm> QualityItems { get; } = new();

        // ── 深渊横幅 ──
        [ObservableProperty] private string _upperDur = "00:00";
        [ObservableProperty] private string _upperDmg = "0";
        [ObservableProperty] private string _upperSub = "等待第一击…";

        // ── 角色详情 ──
        public DetailHeaderVm Detail { get; } = new();
        public ObservableCollection<SkillRowVm> DetailSkills { get; } = new();
        public ObservableCollection<AxisRowVm> AxisRows { get; } = new();

        private readonly DispatcherTimer _pump;         // 250ms,与 event.battle.summary 同频
        private readonly DispatcherTimer _bucketTimer;  // 1s,时间线桶
        private readonly Random _rng = new();
        private double _totalDamage, _taken, _hpCut, _elapsed, _peak;
        private ulong _hits;
        private readonly double[] _weights = { 45.2, 24.8, 15.6, 9.8, 4.6 };

        private static readonly string[] AxisTypes = { "Q", "E", "环合", "普攻", "重击", "闪避反击" };
        private static readonly string[] AxisTargets = { "深渊使徒·终形", "钳兽", "浮游炮·群", "哨戒机兵", "腐蚀种" };

        public DpsAnalysisPageViewModel(ILogService logService)
        {
            _logService = logService;
            SeedCharacters(); SeedSkills(); SeedQuality();
            UpdateDonut();
            RefreshDevices(); // 启动即枚举真实网卡
            _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _pump.Tick += (_, _) => PumpTick();
            _bucketTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _bucketTimer.Tick += (_, _) => BucketTick();
            _logService.LogInfo("[DPS] 页面初始化完成(演示模式 · 模拟数据泵 4Hz)");
        }

        // ══════════ 命令 ══════════
        [RelayCommand]
        private void RefreshDevices()
        {
            Devices.Clear();
            Devices.Add("全部网卡(默认)");

            // 只列物理网卡(以太网 + Wi-Fi),标注连接状态;虚拟网卡/回环/隧道不进列表
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                                                  or NetworkInterfaceType.Wireless80211)
                         .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up))
            {
                var status = ni.OperationalStatus == OperationalStatus.Up ? "已连接" : "未连接";
                Devices.Add($"{ni.Name} - {ni.Description} ({status})");
            }

            SelectedDevice = Devices[0];
            _logService.LogInfo($"[DPS] 网卡列表已刷新(物理网卡 {Devices.Count - 1} 个)");
        }

        [RelayCommand]
        private void StartCapture()
        {
            ResetBattle();
            SeedTimeline();
            FastForwardSeconds(80);          // 开局快进 80s,直接进入"战斗中"观感
            IsRiskView = false; IsDetailView = false; IsLiveView = true;
            AbyssLampOn = true;
            _pump.Start(); _bucketTimer.Start();
            _logService.LogInfo($"[DPS] 采集已启动(演示 · 网卡:{SelectedDevice})");
        }

        [RelayCommand]
        private void StopCapture()
        {
            _pump.Stop(); _bucketTimer.Stop();   // 停止即结算(真实接入后 = capture.stop 排空)
            _logService.LogInfo("[DPS] 采集停止,数据已结算(演示)");
        }

        [RelayCommand]
        private void ClearData()
        {
            ResetBattle();
            TimelineBars.Clear(); _peak = 0;
            _logService.LogInfo("[DPS] 数据已清空,伤害重新累计");
        }

        [RelayCommand]
        private void BackToRisk()
        {
            _pump.Stop(); _bucketTimer.Stop();
            IsDetailView = false; IsLiveView = false; IsRiskView = true;
            _logService.LogInfo("[DPS] 返回风险告知层,采集已停止");
        }

        [RelayCommand]
        private void OpenCharacter(CharacterRowVm? c)
        {
            if (c == null) return;
            Detail.Name = c.Name; Detail.Attribute = c.Attribute; Detail.Initial = c.Initial;
            Detail.TotalStr = c.DamageStr; Detail.AvatarPath = c.AvatarPath;
            DetailSkills.Clear();
            foreach (var s in Skills.Where(s => s.CharName == c.Name)) DetailSkills.Add(s);
            GenerateAxisRows();
            _pump.Stop(); _bucketTimer.Stop();
            IsLiveView = false; IsDetailView = true;
        }

        [RelayCommand]
        private void BackToLive()
        {
            IsDetailView = false; IsLiveView = true;
            _pump.Start(); _bucketTimer.Start();
        }

        // ══════════ 模拟数据泵 ══════════
        private void PumpTick()
        {
            double hit = 8000 + _rng.NextDouble() * 52000;
            var who = Characters[WeightedIndex()];
            _totalDamage += hit; _hits += (ulong)(1 + (_rng.NextDouble() < .3 ? 1 : 0));
            if (_rng.NextDouble() < .25) _taken += 2000 + _rng.NextDouble() * 9000;
            if (_rng.NextDouble() < .12) _hpCut += 3000 + _rng.NextDouble() * 14000;
            _elapsed += 0.25;

            who.Num += hit;
            double sum = Characters.Sum(x => x.Num);
            foreach (var c in Characters)
            {
                c.DamageStr = F(c.Num);
                c.DpsStr = "DPS " + F(c.Num / Math.Max(_elapsed, 0.25));
                c.ShareStr = (sum > 0 ? c.Num / sum * 100 : 0).ToString("0.0") + "%";
                c.BarWidth = (sum > 0 ? c.Num / sum * 100 : 0) * 2.4;
            }

            var mine = Skills.Where(k => k.CharName == who.Name).ToList();
            if (mine.Count > 0) mine[_rng.Next(mine.Count)].Num += hit;
            double skSum = Skills.Sum(k => k.Num);
            foreach (var k in Skills)
            {
                k.DamageStr = F(k.Num);
                k.PctStr = (skSum > 0 ? k.Num / skSum * 100 : 0).ToString("0.0") + "%";
                k.BarWidth = (skSum > 0 ? k.Num / skSum * 100 : 0) * 1.3;
            }

            TotalDamageStr = F(_totalDamage);
            TeamDpsStr = F(_totalDamage / Math.Max(_elapsed, 0.25));
            DurationStr = Ts(_elapsed); ElapsedBadge = Ts(_elapsed);
            DamageTakenStr = F(_taken); HitsStr = _hits.ToString("N0");
            MaxHpReductionStr = F(_hpCut);
            UpperDur = Ts(_elapsed); UpperDmg = F(_totalDamage);
            UpperSub = "DPS " + F(_totalDamage / Math.Max(_elapsed, 0.25))
                     + " · " + string.Join(" / ", Characters.OrderByDescending(x => x.Num).Take(3).Select(x => x.Name));

            UpdateDonut();
        }

        private void BucketTick()
        {
            double v = _rng.NextDouble() < .25 ? 140000 + _rng.NextDouble() * 90000 : 40000 + _rng.NextDouble() * 60000;
            TimelineBars.Add(new BucketBarVm { Kind = "dps", Num = v });
            if (TimelineBars.Count > 176) TimelineBars.RemoveAt(0);
            _peak = Math.Max(_peak, v);
            RescaleBars();
            UpdateLinePoints();
        }

        private void RescaleBars()
        {
            foreach (var b in TimelineBars)
                if (b.Kind == "dps") b.HeightPct = Math.Max(3, b.Num / Math.Max(_peak, 1) * 190);
        }

        private void UpdateLinePoints()
        {
            var line = new PointCollection();
            double hold = 0;
            int n = TimelineBars.Count;
            for (int i = 0; i < n; i++)
            {
                var b = TimelineBars[i];
                double h = b.Kind == "dps" ? b.HeightPct : hold;  // 时停/标记:曲线保持前值
                hold = h;
                line.Add(new System.Windows.Point(i * 10, 200 - h));
            }
            var area = new PointCollection(line);
            if (n > 0)
            {
                area.Add(new System.Windows.Point((n - 1) * 10, 200));
                area.Add(new System.Windows.Point(0, 200));
            }
            LinePoints = line; AreaPoints = area;
        }

        private void UpdateDonut()
        {
            DonutSlices.Clear();
            double sum = Characters.Sum(c => c.Num);
            if (sum <= 0)
            {
                DonutSlices.Add(new DonutSliceVm
                {
                    Geometry = new EllipseGeometry(new System.Windows.Point(110, 110), 100, 100),
                    FillBrush = Freeze("#2A2A3A")
                });
                return;
            }
            const double cx = 110, cy = 110, rO = 100;
            double angle = -90;
            foreach (var c in Characters.OrderByDescending(x => x.Num))
            {
                double sweep = c.Num / sum * 359.999;
                if (sweep < 0.01) continue;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new System.Windows.Point(cx, cy), true, true);
                    g.LineTo(Pt(cx, cy, rO, angle), true, false);
                    g.ArcTo(Pt(cx, cy, rO, angle + sweep), new Size(rO, rO), 0,
                            sweep > 180, SweepDirection.Clockwise, true, false);
                }
                geo.Freeze();
                DonutSlices.Add(new DonutSliceVm { Geometry = geo, FillBrush = AttrBrush(c.Attribute), Label = c.Name });
                angle += sweep;
            }
        }

        private static System.Windows.Point Pt(double cx, double cy, double r, double deg)
        {
            var rad = deg * Math.PI / 180;
            return new System.Windows.Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }

        private static Brush AttrBrush(string? attr) => attr switch
        {
            "相" => Freeze("#FF66B3"),
            "灵" => Freeze("#00F0FF"),
            "光" => Freeze("#FFE600"),
            "暗" => Freeze("#FF4D6D"),
            "咒" => Freeze("#B266FF"),
            "魂" => Freeze("#9EFF6E"),
            _ => Freeze("#667788"),
        };

        private static Brush Freeze(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze(); return b;
        }

        private int WeightedIndex()
        {
            double r = _rng.NextDouble() * 100, acc = 0;
            for (int i = 0; i < _weights.Length; i++) { acc += _weights[i]; if (r <= acc) return i; }
            return 0;
        }

        private void FastForwardSeconds(int sec)
        {
            for (int i = 0; i < sec * 4; i++) PumpTick();
            _elapsed = sec;
        }

        private void ResetBattle()
        {
            _totalDamage = _taken = _hpCut = _elapsed = _peak = 0; _hits = 0;
            foreach (var c in Characters) { c.Num = 0; c.DamageStr = "0"; c.DpsStr = "DPS 0"; c.ShareStr = "0%"; c.BarWidth = 0; }
            foreach (var k in Skills) { k.Num = 0; k.DamageStr = "0"; k.PctStr = "0%"; k.BarWidth = 0; }
            TotalDamageStr = "0"; TeamDpsStr = "0"; DurationStr = "00:00"; ElapsedBadge = "00:00";
            DamageTakenStr = "0"; HitsStr = "0"; MaxHpReductionStr = "0";
            UpperDur = "00:00"; UpperDmg = "0"; UpperSub = "等待第一击…";
            LinePoints = new(); AreaPoints = new(); DonutSlices.Clear();
        }

        private void SeedTimeline()
        {
            TimelineBars.Clear(); _peak = 0;
            for (int i = 0; i < 80; i++)
            {
                bool tstop = (i >= 18 && i <= 20) || (i >= 35 && i <= 36);
                bool marker = i == 30 || i == 58;
                double burst = (i > 38 && i < 52) ? 2.2 : (i > 12 && i < 20) ? 1.5 : 1;
                double v = tstop || marker ? 0 : (82000 + _rng.NextDouble() * 90000) * burst;
                _peak = Math.Max(_peak, v);
                TimelineBars.Add(new BucketBarVm
                {
                    Kind = marker ? "marker" : tstop ? "tstop" : "dps",
                    Num = v,
                    HeightPct = tstop || marker ? 190 : 8
                });
            }
            RescaleBars();
            UpdateLinePoints();
        }

        private void GenerateAxisRows()
        {
            AxisRows.Clear();
            for (int i = 0; i < 28; i++)
            {
                double t = _rng.NextDouble() * Math.Max(_elapsed, 10);
                AxisRows.Add(new AxisRowVm
                {
                    TimeStr = t.ToString("0.00") + "s",
                    TypeStr = AxisTypes[_rng.Next(AxisTypes.Length)],
                    DamageStr = F(2000 + _rng.NextDouble() * 88000),
                    TargetStr = AxisTargets[_rng.Next(AxisTargets.Length)]
                });
            }
        }

        private void SeedCharacters()
        {
            // char_id 为真实角色 ID,头像经 DpsAvatarCatalog 按表解析
            // (1071 卡厄斯 / 1010 娜娜莉 / 1076 真红 / 1004 安魂曲 / 1036 残虹)
            var seed = new[] {
                (1071u, "卡厄斯", "相", 8842310.0), (1010u, "娜娜莉", "灵", 4848130.0),
                (1076u, "真红", "光", 3049630.0), (1004u, "安魂曲", "暗", 1915790.0),
                (1036u, "残虹", "咒", 899060.0),
            };
            foreach (var (id, n, a, v) in seed)
                Characters.Add(new CharacterRowVm(id, n, a) { Num = v });
        }

        private void SeedSkills()
        {
            var seed = new[] {
                ("卡厄斯", "湮灭回响", "相", false, 6333850.0), ("卡厄斯", "终焉序列", "相", true, 2502260.0),
                ("娜娜莉", "潮汐裂斩", "灵", false, 2189480.0), ("真红", "炽阳宣告", "光", false, 1876700.0),
                ("娜娜莉", "深海回涌", "灵", true, 1642110.0), ("安魂曲", "安魂礼拜", "暗", false, 1212030.0),
                ("真红", "裁决之炎", "光", true, 860150.0), ("残虹", "断罪虹光", "咒", false, 606020.0),
            };
            foreach (var (cn, sn, a, fu, v) in seed) Skills.Add(new SkillRowVm(cn, sn, a, fu) { Num = v });
        }

        private void SeedQuality()
        {
            QualityItems.Add(new QualityChipVm("SOURCE", "live", false));
            QualityItems.Add(new QualityChipVm("包", "84,213", false));
            QualityItems.Add(new QualityChipVm("命中包", "3,412", false));
            QualityItems.Add(new QualityChipVm("未知角色命中", "0", false));
            QualityItems.Add(new QualityChipVm("未映射技能行", "2", true));
            QualityItems.Add(new QualityChipVm("服务器修正", "17", false));
            QualityItems.Add(new QualityChipVm("时停区间", "2", false));
            QualityItems.Add(new QualityChipVm("深渊事件", "1", false));
        }

        private void SeedDevices() { Devices.Add("全部网卡(默认)"); SelectedDevice = Devices[0]; }

        private static string F(double v) => Math.Round(v).ToString("N0");
        private static string Ts(double s) => $"{(int)(s / 60):D2}:{(int)(s % 60):D2}";
    }

    // ══════════ 行模型(演示) ══════════
    public partial class CharacterRowVm : ObservableObject
    {
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
            Name = name; Attribute = attribute;
            Initial = name.Length > 0 ? name[..1] : "?";
            AvatarPath = TrustNTE.Helpers.Utils.DpsAvatarCatalog.Resolve(charId);
        }
    }

    public partial class SkillRowVm : ObservableObject
    {
        public string CharName { get; }
        public string SkillName { get; }
        public string Attribute { get; }
        public bool IsFollowUp { get; }
        public double Num { get; set; }
        [ObservableProperty] private string _damageStr = "0";
        [ObservableProperty] private string _pctStr = "0%";
        [ObservableProperty] private double _barWidth;
        public SkillRowVm(string charName, string skillName, string attribute, bool fu)
        { CharName = charName; SkillName = skillName; Attribute = attribute; IsFollowUp = fu; }
    }

    public partial class BucketBarVm : ObservableObject
    {
        public string Kind { get; init; } = "dps";   // dps / tstop(时停灰带) / marker(深渊标线)
        public double Num { get; set; }
        [ObservableProperty] private double _heightPct = 8;
    }

    public class QualityChipVm
    {
        public string Label { get; }
        public string ValueStr { get; }
        public bool IsWarn { get; }
        public QualityChipVm(string label, string value, bool warn)
        { Label = label; ValueStr = value; IsWarn = warn; }
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
        [ObservableProperty] private string? _avatarPath;
        public bool HasAvatar => AvatarPath != null;
    }

    public partial class DonutSliceVm : ObservableObject
    {
        [ObservableProperty] private Geometry _geometry = Geometry.Empty;
        [ObservableProperty] private Brush _fillBrush = Brushes.DimGray;
        public string Label { get; init; } = "";
    }
}