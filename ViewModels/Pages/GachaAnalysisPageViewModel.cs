using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpPcap.LibPcap;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using TrustNTE.Models.Gacha;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.ViewModels.Pages
{
    /// <summary>
    /// 抽卡分析 VM：风险门禁 + 会话状态 + 记录流 + 池统计（对标参考图）。
    /// 日志统一进全局「运行日志」控制台（ILogService），页面内无日志面板。
    /// 网卡下拉框：默认「全部网卡」；选中具体网卡后，配置带 NicName 传给引擎只监听这一张。
    /// </summary>
    public partial class GachaAnalysisPageViewModel : ObservableObject
    {
        private readonly IGachaCaptureService _captureService;
        private readonly ILogService _logService;
        private readonly IDispatcherService _dispatcherService;

        private CancellationTokenSource? _sessionCts;

        // ── 门禁与会话状态 ──
        [ObservableProperty] private bool _isRiskAccepted;
        [ObservableProperty] private bool _isWorking;
        [ObservableProperty] private string _statusText = "";
        [ObservableProperty] private string _engineTag = "引擎待机";
        [ObservableProperty] private string _elapsedText = "00:00";

        // ── 网卡选择 ──
        public ObservableCollection<NicOption> NicOptions { get; } = new();
        [ObservableProperty] private NicOption? _selectedNic;
        [ObservableProperty] private string _nicStatusText = "";

        // ── 计数器 ──
        [ObservableProperty] private long _capturedPackets;
        [ObservableProperty] private long _decodedPackets;
        [ObservableProperty] private long _recordCount;

        // ── 统计 ──
        [ObservableProperty] private PoolSummary? _selectedPool;

        public ObservableCollection<GachaRecord> Records { get; } = new();
        public ObservableCollection<PoolSummary> PoolSummaries { get; } = new();
        public ObservableCollection<SRankDrop> SRankDrops { get; } = new();

        public bool IsRiskLayerVisible => !IsWorking;
        public bool IsWorkLayerVisible => IsWorking;

        partial void OnIsWorkingChanged(bool value)
        {
            OnPropertyChanged(nameof(IsRiskLayerVisible));
            OnPropertyChanged(nameof(IsWorkLayerVisible));
        }

        partial void OnSelectedPoolChanged(PoolSummary? value) => RebuildSRankDrops();

        public GachaAnalysisPageViewModel(
            IGachaCaptureService captureService,
            ILogService logService,
            IDispatcherService dispatcherService)
        {
            _captureService = captureService;
            _logService = logService;
            _dispatcherService = dispatcherService;

            // 服务事件全部经 IDispatcherService 切回 UI 线程（红线2）
            _captureService.EngineInfoChanged += info =>
                _dispatcherService.BeginInvoke(() => EngineTag = info);
            _captureService.CountersUpdated += (captured, decoded) =>
                _dispatcherService.BeginInvoke(() => { CapturedPackets = captured; DecodedPackets = decoded; });
            _captureService.RecordDecoded += record =>
                _dispatcherService.BeginInvoke(() =>
                {
                    Records.Insert(0, record);
                    RecordCount = Records.Count;
                    RecomputePoolSummaries();
                    RebuildSRankDrops();
                });

            InitPoolSummaries();
            RefreshNics();   // 启动即枚举网卡供下拉框选择（Npcap 未装时只提示不炸）
            AppendLog("抽卡分析模块已加载 · 抓包引擎就绪");
        }

        // ══════════ 网卡枚举（下拉框） ══════════

        /// <summary>
        /// 枚举本机 pcap 网卡。首项固定「全部网卡」保持旧行为；
        /// Npcap 不可用时给出提示并回落到「全部网卡」，不抛异常不炸 UI。
        /// </summary>
        [RelayCommand]
        private void RefreshNics()
        {
            var prev = SelectedNic?.DeviceName;
            NicOptions.Clear();
            NicOptions.Add(new NicOption { DeviceName = "", DisplayName = "全部网卡（默认）" });

            try
            {
                var devices = LibPcapLiveDeviceList.Instance;
                foreach (var d in devices)
                {
                    var display = string.IsNullOrEmpty(d.Description) ? d.Name : d.Description;
                    NicOptions.Add(new NicOption { DeviceName = d.Name, DisplayName = display });
                }
                SelectedNic = NicOptions.FirstOrDefault(n => n.DeviceName == prev) ?? NicOptions[0];
                NicStatusText = $"已加载 {devices.Count} 张网卡 · 建议选游戏流量所在的真实网卡（任务管理器→性能 里看哪张在跑）";
            }
            catch (Exception ex)
            {
                SelectedNic = NicOptions[0];
                NicStatusText = $"网卡列表不可用：{ex.Message}（多为 Npcap 未安装，装好后点「刷新」）";
            }
        }

        // ══════════ 池统计（对标参考图） ══════════

        private void InitPoolSummaries()
        {
            PoolSummaries.Clear();
            PoolSummaries.Add(new PoolSummary { Kind = "limited", Name = "限定卡池", HardPity = 90, IsSelected = true });
            PoolSummaries.Add(new PoolSummary { Kind = "standard", Name = "常驻卡池", HardPity = 90 });
            PoolSummaries.Add(new PoolSummary { Kind = "arc", Name = "弧盘池", HardPity = 60 });
            SelectedPool = PoolSummaries[0];
        }

        /// <summary>池归类：弧盘=fork；monopoly 且 lottery_id=Lottery_Permanent → 常驻；其余 → 限定（规格 §9.3）。</summary>
        private static string ClassifyKind(GachaRecord r) => r.RecordType switch
        {
            "fork" => "arc",
            "monopoly" when r.LotteryId == "Lottery_Permanent" => "standard",
            _ => "limited",
        };

        private void RecomputePoolSummaries()
        {
            foreach (var p in PoolSummaries)
            {
                var recs = Records.Where(r => ClassifyKind(r) == p.Kind).ToList();
                p.TotalPulls = recs.Count;
                p.FiveStars = recs.Count(r => r.Rarity == 5);
                p.AvgPerFive = p.FiveStars > 0
                    ? ((double)p.TotalPulls / p.FiveStars).ToString("F1")
                    : "0.0";
            }
        }

        /// <summary>S级出货列表：当前选中池按时间正序遍历，相邻两个5★之间的抽数即该条的「N 抽」。</summary>
        private void RebuildSRankDrops()
        {
            SRankDrops.Clear();
            var pool = SelectedPool;
            if (pool is null) return;

            int streak = 0;
            foreach (var r in Records.Where(r => ClassifyKind(r) == pool.Kind).OrderBy(r => r.Time))
            {
                streak++;
                if (r.Rarity == 5)
                {
                    SRankDrops.Add(new SRankDrop
                    {
                        DateText = r.Time.ToString("MM-dd"),
                        ItemName = string.IsNullOrEmpty(r.ItemName) ? r.ItemId : r.ItemName,
                        HardPity = pool.HardPity,
                        ItemId = r.ItemId,
                        PoolKind = pool.Kind,
                        Pulls = streak,


                    });

                    streak = 0;
                }
            }
        }

        [RelayCommand]
        private void SelectPool(PoolSummary? pool)
        {
            if (pool is null) return;
            foreach (var p in PoolSummaries) p.IsSelected = false;
            pool.IsSelected = true;
            SelectedPool = pool;
        }

        // ══════════ 会话控制 ══════════

        [RelayCommand]
        private async Task StartCaptureAsync()
        {
            if (IsWorking || !IsRiskAccepted) return;

            _sessionCts = new CancellationTokenSource();
            var token = _sessionCts.Token;

            IsWorking = true;
            StatusText = "";
            EngineTag = "引擎初始化中…";
            _logService.LogInfo("[抽卡] 发起抓包会话");

            _ = RunSessionTimerAsync(token);

            try
            {
                // 每次启动新建配置：带上下拉框选中的网卡（null/空 = 全部网卡）
                var cfg = new GachaCaptureConfig { NicName = SelectedNic?.DeviceName };
                await _captureService.StartAsync(cfg, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // ★ 启动失败立即弹回风险层，错误显示在启动按钮下方
                EngineTag = "引擎待机";
                StatusText = ex.Message;
                IsWorking = false;
                _logService.LogWarning($"[抽卡] 启动反馈：{ex.Message}");
            }
        }

        [RelayCommand]
        private async Task StopCaptureAsync()
        {
            try { await _captureService.StopAsync(); } catch { }
            _sessionCts?.Cancel();
            EngineTag = "引擎已停止";
            IsWorking = false;   // ★ 返回风险层（启动按钮在此层）
            StatusText = "";
            _logService.LogInfo($"[抽卡] 会话已停止 · 记录 {Records.Count} 条");
        }

        [RelayCommand]
        private void ExportJson()
        {
            if (Records.Count == 0)
            {
                StatusText = "暂无记录可导出";
                return;
            }
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "JSON|*.json",
                    FileName = $"抽卡记录_{DateTime.Now:yyyyMMdd_HHmmss}.json"
                };
                if (dialog.ShowDialog() != true) return;

                var payload = new
                {
                    info = new
                    {
                        schema = "nte-gacha-export",
                        schema_version = "2.1", 
                        export_app = "TrustNTE",
                        export_timestamp = DateTime.Now
                    },
                    nte = new { list = Records }
                };
                File.WriteAllText(dialog.FileName,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
                StatusText = $"已导出 {Records.Count} 条 → {dialog.FileName}";
                _logService.LogInfo($"[抽卡] 已导出 {Records.Count} 条记录 → {dialog.FileName}");
            }
            catch (Exception ex)
            {
                StatusText = $"导出失败：{ex.Message}";
                _logService.LogError($"[抽卡] 导出失败：{ex.Message}");
            }
        }

        private async Task RunSessionTimerAsync(CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);
                    _dispatcherService.BeginInvoke(() =>
                        ElapsedText = $"{(int)sw.Elapsed.TotalMinutes:D2}:{sw.Elapsed.Seconds:D2}");
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>S级出货条目（右侧一览）：日期/名称/抽数/保底 + 头像（原版同款，来自 core.json asset_refs → NTE_Assets）。</summary>
        public partial class SRankDrop : ObservableObject
        {
            public string DateText { get; init; } = "";
            public string ItemName { get; init; } = "";
            public string ItemId { get; init; } = "";
            public string PoolKind { get; init; } = "limited";
            public int Pulls { get; init; }
            public int HardPity { get; init; }

            /// <summary>欧非档位（good=欧 / warn=非 / danger=超非）。阈值原文：前端 dashboardUi.ts fiveWallPityTone。</summary>
            public string PityTone => PoolKind == "arc"
                ? (Pulls > 50 ? "danger" : Pulls > 30 ? "warn" : "good")
                : (Pulls >= 90 ? "danger" : Pulls > 70 ? "warn" : "good");

            /// <summary>仅 danger 显示「超非」标签（参考图视觉）。</summary>
            public bool IsDanger => PityTone == "danger";

            private ImageSource? _avatar;
            public ImageSource? Avatar => _avatar ??= LoadAvatar();

            private ImageSource? LoadAvatar()
            {
                var path = TrustNTE.Services.Gacha.Maps.GachaMapCatalog.ItemAvatarPath(ItemId);
                if (string.IsNullOrEmpty(path)) return null;
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // 不锁文件
                    bitmap.UriSource = new System.Uri(path, System.UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
                catch { return null; }
            }
        }



        /// <summary>统一日志出口：全部写入全局「运行日志」控制台（页面内无日志面板）。</summary>
        public void AppendLog(string message) => _logService.LogInfo($"[抽卡] {message}");
    }
}
