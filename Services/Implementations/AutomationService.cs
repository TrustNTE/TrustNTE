using TrustNTE.Models;
using OpenCvSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using TrustNTE.Helpers;
using TrustNTE.Services.Interfaces;
using TrustNTE.Services.Triggers;
using TrustNTE.Views.Windows;
using TrustNTE.Services.Implementations;

namespace TrustNTE.Services
{
    public partial class AutomationService : IAutomationContext
    {
        private readonly IGameService _gameService;
        private readonly LauncherService _launcherService;
        private readonly IConfigService _configService;
        private readonly ILogService _logService;
        private readonly IInputService _inputService;
        private readonly INteInteractionService _nteInteractionService;
        private readonly IInputRouter _router;
        private readonly ICoordinateMapper _coordMapper;
        private readonly NteRenderContextManager _nteRenderCtx;
        private readonly GameSupervisorService _supervisor;

        private readonly SemaphoreSlim _workflowLock = new(1, 1);
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _dispatchCts;
        private Task? _workflowTask;

        private readonly SemaphoreSlim _captureGate = new(1, 1);
        private readonly CancellationTokenSource _idleGuardCts = new();

        private volatile bool _captureEverStarted;
        private int _idleGuardStarted;
        private string? _lastLoggedClickMode;

        private DateTime _lastStaleWarnTime = DateTime.MinValue;
        private DateTime _engineStartUtc = DateTime.MinValue;
        private long _frameCount;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;
        private long _heartbeatBaseFrames;

        private volatile bool _everBoundGame;
        private DateTime _lastUserEngineActionUtc = DateTime.MinValue;

        public event Action<string>? OnLogReceived;
        public event Action<bool>? OnRunningStateChanged;
        public event Action<BitmapSource>? OnFrameCaptured;
        public event Action<BitmapSource>? OnCombatDebugFrameCaptured;
        public event Action? OnWindowBound;

        public IntPtr CurrentHwnd { get; private set; } = IntPtr.Zero;
        public bool IsPreviewWindowOpen { get; set; } = false;

        public GameSupervisorService Supervisor => _supervisor;
        public NteGameStateTemplateService GameStateTemplates => _gameStateTemplates;
        public TriggerManager Triggers => _triggerManager;
        public bool IsEngineRunning => _cts is { IsCancellationRequested: false };
        public bool IsEngineBusy => _workflowLock.CurrentCount == 0;

        private readonly NteGameStateTemplateService _gameStateTemplates;
        private readonly TriggerManager _triggerManager = new();
        private readonly YoloModelProvider _yoloProvider;
        private Mat? _latestFrame;
        private readonly object _frameLock = new();

        private int _lastTrackedW, _lastTrackedH;
        private double _lastTrackedDpi = 1.0;
        private DateTime _lastResizeLogAt = DateTime.MinValue;
        private int _isBizRunning = 0;
        private int _isUiRunning = 0;
        private int _isCombatDebugRunning = 0;
        private readonly AutoStoryTemplateService _storyTemplateService;
        private readonly NteWordFTemplateService _wordFTemplates;
        private readonly IOcrService _ocrService;

        public AutomationService(
            IGameService gameService, LauncherService launcherService, IConfigService configService,
            ILogService logService, IInputService inputService, INteInteractionService nteInteractionService,
            AutoStoryTemplateService storyTemplateService, IInputRouter router, ICoordinateMapper coordMapper,
            NteRenderContextManager nteRenderCtx, GameSupervisorService supervisor, NteGameStateTemplateService gameStateTemplates,
            IOcrService ocrService)
        {
            _gameService = gameService;
            _launcherService = launcherService;
            _configService = configService;
            _logService = logService;
            _inputService = inputService;
            _nteInteractionService = nteInteractionService;
            _storyTemplateService = storyTemplateService;
            _router = router;
            _coordMapper = coordMapper;
            _nteRenderCtx = nteRenderCtx;
            _supervisor = supervisor;
            _gameStateTemplates = gameStateTemplates;
            _ocrService = ocrService;
            _wordFTemplates = new NteWordFTemplateService(logService);

            _yoloProvider = new YoloModelProvider(CreateCombatYoloDetector, _logService);
            _logService.OnLogReceived += (msg) => OnLogReceived?.Invoke(msg);

            _supervisor.ShouldAbortRestart = () =>
                IsEngineRunning
                || _workflowLock.CurrentCount == 0
                || (DateTime.UtcNow - _lastUserEngineActionUtc).TotalSeconds < 30;
        }

        private void LogClickModeIfChanged(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode) || mode == _lastLoggedClickMode) return;
            _lastLoggedClickMode = mode;
            _logService.LogInfo($"[输入] 已启用【{mode}】");
        }
        private ICombatYoloDetector CreateCombatYoloDetector(bool useGpu)
        {
            return new OnnxYoloDetectorAdapter(
                GameConstants.CombatYoloModelPath,
                useGpu,
                msg => _logService.LogInfo(msg));
        }
        public YoloModelProvider YoloProvider => _yoloProvider;

        private TriggerContext BuildTriggerContext(IntPtr hwnd) => new()
        {
            Automation = this,
            Log = _logService,
            Config = _configService,
            Input = _inputService,
            NteInteraction = _nteInteractionService,
            Router = _router,
            Hwnd = hwnd,
            StoryTemplates = _storyTemplateService,
            YoloProvider = _yoloProvider,
            LegacyCoordMapper = (lx, ly) => MapLogical(lx, ly),
            WordFTemplates = _wordFTemplates,
            OcrRecognize = mat => _ocrService.RecognizeTextAsync(mat)!,
        };

        public (int Px, int Py) MapLogical(int lx, int ly) => _coordMapper.LogicalToClient(CurrentHwnd, lx, ly);

        public void ClickLogical(IntPtr hwnd, int lx, int ly, bool isLeft = true)
        {
            var (px, py) = _coordMapper.LogicalToClient(hwnd, lx, ly);
            _nteInteractionService.NteClick(hwnd, px, py, isLeft);
        }

        public void EnableCombatTriggers()
        {
            try
            {
                var cfg = _configService.Load();
                if (cfg.AutoCombatEnabled) _yoloProvider.EnsureStartLoading(cfg.YoloUseGpu);
            }
            catch { }
            _triggerManager.EnableCombatTriggers(_logService);
        }

        public Mat? GetLatestFrame() { lock (_frameLock) return _latestFrame?.Clone(); }

        public bool TryGetClientRegion(out int x, out int y, out int w, out int h, out double dpiScale)
        {
            x = y = w = h = 0; dpiScale = 1.0;
            if (CurrentHwnd == IntPtr.Zero) return false;
            if (!Win32Input.GetClientRect(CurrentHwnd, out var rc)) return false;
            var pt = new Win32Input.POINT { X = 0, Y = 0 };
            if (!Win32Input.ClientToScreen(CurrentHwnd, ref pt)) return false;
            x = pt.X; y = pt.Y; w = rc.Right - rc.Left; h = rc.Bottom - rc.Top;
            dpiScale = Win32Input.GetDpiForWindow(CurrentHwnd) / 96.0;
            return w > 0 && h > 0;
        }
    }
}
