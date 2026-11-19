using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrustNTE.Models.Dps;
using TrustNTE.Services.Interfaces;

using PointCollection = System.Windows.Media.PointCollection;

namespace TrustNTE.ViewModels.Pages
{
    /// <summary>
    /// DPS 统计页 VM：真实 Core 命令与视图状态机。
    /// 图表几何、Core 事件映射分别位于 ChartArt / Lifecycle 分部文件。
    /// </summary>
    public partial class DpsAnalysisPageViewModel : ObservableObject, IDisposable
    {
        private readonly IDpsCoreService _dpsCore;
        private readonly IDispatcherService _dispatcherService;
        private readonly ILogService _logService;
        private readonly IConfigService _configService;
        private readonly DispatcherTimer _timelineTimer;
        private readonly DispatcherTimer _processTimer;

        // 系统清理边界：关窗时最多等待 5 秒，随后由 Core 服务有界终止进程。
        private const int CloseTimeoutMs = 5_000;

        private CancellationTokenSource _windowCts = new();
        private bool _operationInProgress;
        private bool _resumeAfterDetail;
        private bool _characterCatalogFailed;
        private bool _disposed;

        [ObservableProperty] private bool _isRiskView = true;
        [ObservableProperty] private bool _isLiveView;
        [ObservableProperty] private bool _isDetailView;
        [ObservableProperty] private bool _isOperationInProgress;

        [ObservableProperty] private bool _isBarMode = true;
        [ObservableProperty] private bool _isLineMode;
        [ObservableProperty] private bool _isDonutMode;
        [ObservableProperty] private PointCollection _linePoints = new();
        [ObservableProperty] private PointCollection _areaPoints = new();
        public ObservableCollection<DonutSliceVm> DonutSlices { get; } = new();

        [ObservableProperty] private bool _acceptedRisk;

        /// <summary>勾选即写配置,下次启动自动带上。</summary>
        partial void OnAcceptedRiskChanged(bool value)
        {
            if (_disposed) return;
            _configService.Update(config => config.RiskAccepted = value);
            _logService.LogInfo($"[DPS] 风险告知勾选已记忆:{(value ? "已确认" : "已撤销")}");
        }
        [ObservableProperty] private DpsDeviceOption? _selectedDevice;
        public ObservableCollection<DpsDeviceOption> Devices { get; } = new();

        [ObservableProperty] private string _totalDamageStr = "0";
        [ObservableProperty] private string _teamDpsStr = "0";
        [ObservableProperty] private string _durationStr = "00:00";
        [ObservableProperty] private string _damageTakenStr = "0";
        [ObservableProperty] private string _hitsStr = "0";
        [ObservableProperty] private string _maxHpReductionStr = "0";
        [ObservableProperty] private string _elapsedBadge = "00:00";

        [ObservableProperty] private bool _coreLampOn;
        [ObservableProperty] private bool _gameLampOn;
        [ObservableProperty] private bool _abyssLampOn;
        [ObservableProperty] private bool _isCapturing;
        [ObservableProperty] private string _captureStatusText = "尚未连接 DPS Core。";

        public ObservableCollection<CharacterRowVm> Characters { get; } = new();
        public ObservableCollection<SkillRowVm> Skills { get; } = new();
        public ObservableCollection<BucketBarVm> TimelineBars { get; } = new();
        public ObservableCollection<QualityChipVm> QualityItems { get; } = new();

        [ObservableProperty] private string _abyssFloorText = "--F";
        [ObservableProperty] private string _abyssStateText = "ABYSS · 未检测";
        [ObservableProperty] private string _upperDur = "00:00";
        [ObservableProperty] private string _upperDmg = "0";
        [ObservableProperty] private string _upperSub = "等待上半数据…";
        [ObservableProperty] private string _lowerDur = "00:00";
        [ObservableProperty] private string _lowerDmg = "0";
        [ObservableProperty] private string _lowerSub = "等待下半数据…";

        public DetailHeaderVm Detail { get; } = new();
        public ObservableCollection<SkillRowVm> DetailSkills { get; } = new();
        public ObservableCollection<AxisRowVm> AxisRows { get; } = new();

        public DpsAnalysisPageViewModel(
    IDpsCoreService dpsCore,
    IDispatcherService dispatcherService,
    ILogService logService,
    IConfigService configService)
        {
            _dpsCore = dpsCore;
            _dispatcherService = dispatcherService;
            _logService = logService;
            _configService = configService;
            _acceptedRisk = _configService.Load().RiskAccepted;

            _dpsCore.BattleSummaryReceived += OnBattleSummaryReceived;
            _dpsCore.CaptureStatusReceived += OnCaptureStatusReceived;
            _dpsCore.CoreStateChanged += OnCoreStateChanged;
            _dpsCore.DiagnosticReceived += OnCoreDiagnosticReceived;

            Devices.Add(CreateAutoDeviceOption());
            SelectedDevice = Devices[0];
            UpdateDonut();

            // 有界 UI pacing：时间线 1Hz；游戏进程灯仅做低频状态提示，不参与抓包决策。
            _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timelineTimer.Tick += (_, _) => RefreshTimelineCommand.Execute(null);
            _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _processTimer.Tick += (_, _) => ProbeGameProcess();
            // 首次进程探测和 Core 握手由 PrepareForOpen() 触发。

            _logService.LogInfo("[DPS] 页面初始化完成（Nte-Dps.exe 真实 Core 模式）。");

        }

        public void PrepareForOpen()
        {
            if (_windowCts.IsCancellationRequested)
            {
                _windowCts.Dispose();
                _windowCts = new CancellationTokenSource();
            }
            if (!_processTimer.IsEnabled) _processTimer.Start();
            ProbeGameProcess();
            RefreshDevicesCommand.Execute(null);
        }

        [RelayCommand]
        private async Task RefreshDevicesAsync()
        {
            if (!BeginOperation()) return;
            try
            {
                CaptureStatusText = "正在握手并探测 Npcap 网卡…";
                var detected = await _dpsCore.DetectAsync(_windowCts.Token);
                var previousName = SelectedDevice?.Name;

                Devices.Clear();
                Devices.Add(CreateAutoDeviceOption());
                foreach (var device in detected.Devices)
                {
                    var addresses = device.Ipv4.Count > 0
                        ? $" · {string.Join(" / ", device.Ipv4)}"
                        : "";
                    Devices.Add(new DpsDeviceOption(
                        device.Name,
                        $"{device.Description}{addresses}"));
                }

                SelectedDevice = Devices.FirstOrDefault(device => device.Name == previousName)
                                 ?? Devices.FirstOrDefault(device => device.Name == detected.RecommendedDevice)
                                 ?? Devices[0];
                GameLampOn = detected.GameProcessDetected;
                CaptureStatusText = detected.GameProcessDetected
                    ? $"Core {_dpsCore.CoreVersion} 就绪；检测到 {detected.Devices.Count} 个 Npcap 网卡。"
                    : $"Core {_dpsCore.CoreVersion} 就绪；暂未检测到游戏连接，可选手动网卡。";
            }
            catch (Exception ex)
            {
                HandleCommandException(ex, "探测运行环境");
            }
            finally
            {
                EndOperation();
            }
        }

        [RelayCommand]
        private async Task StartCaptureAsync()
        {
            if (!AcceptedRisk)
            {
                CaptureStatusText = "请先阅读并勾选风险告知。";
                return;
            }
            if (!BeginOperation()) return;

            try
            {
                CaptureStatusText = "正在重置旧战报并启动真实抓包…";
                await _dpsCore.ResetBattleAsync(_windowCts.Token);
                await _dpsCore.StartCaptureAsync(SelectedDevice?.Name, _windowCts.Token);

                ResetBattle();
                IsRiskView = false;
                IsDetailView = false;
                IsLiveView = true;
                IsCapturing = true;
                CaptureStatusText = "抓包运行中，等待有效战斗数据。";
                _timelineTimer.Start();
                _logService.LogInfo($"[DPS] 采集已启动，网卡：{SelectedDevice?.DisplayName ?? "自动"}。");
            }
            catch (Exception ex)
            {
                HandleCommandException(ex, "启动抓包");
            }
            finally
            {
                EndOperation();
            }
        }

        [RelayCommand]
        private async Task StopCaptureAsync()
        {
            if (!BeginOperation()) return;
            try
            {
                await _dpsCore.StopCaptureAsync(_windowCts.Token);
                _timelineTimer.Stop();
                IsCapturing = false;
                await RefreshTimelineAsync();
                CaptureStatusText = "采集已停止；最终战报已保留，可点击角色查看详情。";
            }
            catch (Exception ex)
            {
                HandleCommandException(ex, "停止抓包");
            }
            finally
            {
                EndOperation();
            }
        }

        [RelayCommand]
        private async Task ClearDataAsync()
        {
            if (!BeginOperation()) return;
            try
            {
                if (_dpsCore.IsCoreRunning)
                    await _dpsCore.ResetBattleAsync(_windowCts.Token);
                ResetBattle();
                CaptureStatusText = IsCapturing
                    ? "战斗数据已清空，抓包继续运行。"
                    : "战斗数据已清空。";
            }
            catch (Exception ex)
            {
                HandleCommandException(ex, "清空战斗数据");
            }
            finally
            {
                EndOperation();
            }
        }

        [RelayCommand]
        private async Task BackToRiskAsync()
        {
            if (!BeginOperation()) return;
            try
            {
                _timelineTimer.Stop();
                await _dpsCore.ShutdownAsync(_windowCts.Token);
                IsCapturing = false;
                IsDetailView = false;
                IsLiveView = false;
                IsRiskView = true;
                CaptureStatusText = "Core 已关闭；重新启动抓包时会建立新会话。";
            }
            catch (Exception ex)
            {
                HandleCommandException(ex, "关闭 Core 会话");
            }
            finally
            {
                EndOperation();
            }
        }

        [RelayCommand]
        private async Task OpenCharacterAsync(CharacterRowVm? character)
        {
            if (character == null || !BeginOperation()) return;
            try
            {
                _resumeAfterDetail = _dpsCore.IsCaptureRunning;
                if (_resumeAfterDetail)
                    await _dpsCore.StopCaptureAsync(_windowCts.Token);
                _timelineTimer.Stop();
                IsCapturing = false;

                Detail.Name = character.Name;
                Detail.Attribute = character.Attribute;
                Detail.Initial = character.Initial;
                Detail.TotalStr = character.DamageStr;
                Detail.AvatarPath = character.AvatarPath;

                DetailSkills.Clear();
                foreach (var skill in Skills.Where(skill => skill.CharId == character.CharId))
                    DetailSkills.Add(skill);
                AxisRows.Clear();

                IsLiveView = false;
                IsRiskView = false;
                IsDetailView = true;
                CaptureStatusText = "正在读取 Core 逐击分页…";
                await LoadCharacterAxisAsync(character.CharId, _windowCts.Token);
                CaptureStatusText = $"已载入 {character.Name} 的 {AxisRows.Count} 条输出逐击。";
            }
            catch (Exception ex)
            {
                HandleCommandException(ex, "读取角色详情");
            }
            finally
            {
                EndOperation();
            }
        }

        [RelayCommand]
        private async Task BackToLiveAsync()
        {
            if (!BeginOperation()) return;
            try
            {
                IsDetailView = false;
                IsRiskView = false;
                IsLiveView = true;

                if (_resumeAfterDetail && _dpsCore.IsCoreRunning)
                {
                    await _dpsCore.StartCaptureAsync(SelectedDevice?.Name, _windowCts.Token);
                    IsCapturing = true;
                    _timelineTimer.Start();
                    CaptureStatusText = "已恢复原战斗会话的抓包。";
                }
                else
                {
                    CaptureStatusText = "返回战报；采集保持停止。";
                }
                _resumeAfterDetail = false;
            }
            catch (Exception ex)
            {
                HandleCommandException(ex, "恢复抓包");
            }
            finally
            {
                EndOperation();
            }
        }

        [RelayCommand]
        private async Task CloseAsync()
        {
            _windowCts.Cancel();
            _timelineTimer.Stop();
            _processTimer.Stop();
            try
            {
                using var timeout = new CancellationTokenSource(CloseTimeoutMs);
                await _dpsCore.ShutdownAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                _logService.LogDebug($"[DPS] 关窗收尾：{ex.Message}");
            }
            finally
            {
                IsCapturing = false;
                CoreLampOn = false;
                IsLiveView = false;
                IsDetailView = false;
                IsRiskView = true;
                _operationInProgress = false;
                IsOperationInProgress = false;
            }
        }

        private bool BeginOperation()
        {
            if (_operationInProgress || _disposed) return false;
            _operationInProgress = true;
            IsOperationInProgress = true;
            return true;
        }

        private void EndOperation()
        {
            _operationInProgress = false;
            IsOperationInProgress = false;
        }

        private static DpsDeviceOption CreateAutoDeviceOption()
            => new(null, "全部网卡（由 Core 自动选择）");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timelineTimer.Stop();
            _processTimer.Stop();
            _windowCts.Cancel();

            _dpsCore.BattleSummaryReceived -= OnBattleSummaryReceived;
            _dpsCore.CaptureStatusReceived -= OnCaptureStatusReceived;
            _dpsCore.CoreStateChanged -= OnCoreStateChanged;
            _dpsCore.DiagnosticReceived -= OnCoreDiagnosticReceived;
            _windowCts.Dispose();
        }
    }
}
