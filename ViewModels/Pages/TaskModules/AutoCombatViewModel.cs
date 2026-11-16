using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Helpers;
using TrustNTE.Models.TaskConfigs;
using TrustNTE.Services;
using TrustNTE.Services.Interfaces;
using TrustNTE.Services.Triggers;

namespace TrustNTE.ViewModels.Pages.TaskModules
{
    public partial class AutoCombatViewModel : TaskModuleViewModelBase
    {
        private readonly IGameService _gameService;
        private readonly IInputRouter _router;          // ★ 新增：统一输入路由
        private readonly AutomationService _automationService;
        private readonly IDialogService _dialogService;
        private CancellationTokenSource? _taskCts;
        private bool _isTargetLocked = false;

        public AutoCombatConfig Config { get; }

        public AutoCombatViewModel(
            IConfigService configService,
            ILogService logService,
            IGameService gameService,
            IInputRouter router,                        // ★ 新增
            AutomationService automationService,
            IDialogService dialogService)
            : base(logService, configService, "自动战斗")
        {
            _gameService = gameService;
            _router = router;
            _automationService = automationService;
            _dialogService = dialogService;
            // [Phase 1] 持久化由基类接管
            Config = BindConfig(c => c.Combat, (main, cfg) => main.Combat = cfg);
        }

        protected override async Task ExecuteTaskAsync(CancellationToken token)
        {
            if (_automationService.CurrentHwnd == IntPtr.Zero)
            {
                _logService.LogError("无法启动自动战斗：请先开启截图器。");
                return;
            }

            // [架构] 统一走引擎侧 YoloModelProvider（单实例、进游戏才加载、失败自动重试）
            var yolo = _automationService.YoloProvider;
            if (yolo.State == YoloModelProvider.YoloLoadState.Failed)
            {
                _logService.LogError($"[自动战斗] YOLO 模型加载失败，本次无法执行：{yolo.StatusText}（重开截图引擎进游戏后会自动重试）");
                return;
            }

            if (yolo.State == YoloModelProvider.YoloLoadState.NotStarted)
            {
                yolo.EnsureStartLoading(true);
                _logService.Log("[自动战斗] YOLO 模型后台加载中，就绪前不索敌（不会误点）");
            }

            _taskCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var localToken = _taskCts.Token;
            _isTargetLocked = false;

            try
            {
                while (!localToken.IsCancellationRequested)
                {
                    using var frame = _gameService.CaptureWindow();
                    if (frame == null || frame.Empty())
                    {
                        await Task.Delay(20, localToken);
                        continue;
                    }

                    // 模型未就绪时 Detect 返回 null → 按无目标处理，静默等待加载完成
                    var targets = yolo.Detect(frame, (float)Config.ConfidenceThreshold);
                    if (targets != null && targets.Count > 0)
                    {
                        var target = targets.OrderBy(t => Math.Abs(t.Rect.X + t.Rect.Width / 2 - frame.Width / 2)
                                                       + Math.Abs(t.Rect.Y + t.Rect.Height / 2 - frame.Height / 2)).First();

                        int screenX = target.Rect.X + target.Rect.Width / 2;
                        int screenY = target.Rect.Y + target.Rect.Height / 2;

                        if (!_isTargetLocked)
                        {
                            LockTarget(screenX, screenY);
                            _logService.Log($"已锁定目标: ({screenX}, {screenY})");
                            _isTargetLocked = true;
                        }

                        await PerformCombatActions(localToken);
                    }
                    else
                    {
                        _isTargetLocked = false;
                    }

                    await Task.Delay(100, localToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logService.LogError($"战斗异常: {ex.Message}");
            }
            finally
            {
                _taskCts?.Dispose();
                _taskCts = null;
            }
        }

        private async Task PerformCombatActions(CancellationToken token)
        {
            SmartKeyPress(KeyCode.Q, 50);
            await Task.Delay(200, token);

            SmartKeyPress(KeyCode.E, 50);
            await Task.Delay(200, token);

            // 平A（960,540 = 1080P 中心点）
            NormalClick(960, 540);
            await Task.Delay(200, token);
        }

        // ══════════ 输入统一走 Router（修复：映射串线 / 驱动割裂 / 卡键裸奔）══════════

        /// <summary>驱动选择：与 CombatStrategyEngine.DriveFor 逐字对齐（战斗后台模式 → 强制后台）。</summary>
        private InputDriveType ResolveDrive()
            => _configService.Load().CombatBackgroundMode
                ? InputDriveType.Background
                : _router.CurrentDrive;

        /// <summary>锁定目标：中键 + 物理坐标（本 VM 用原始帧，YOLO 坐标即物理坐标，不做归一化换算）。</summary>
        private void LockTarget(int x, int y)
            => _router.MiddleClickWith(ResolveDrive(), _automationService.CurrentHwnd, x, y);

        /// <summary>普通点击（平A）：左键 + 物理坐标。</summary>
        private void NormalClick(int x, int y)
            => _router.ClickWith(ResolveDrive(), _automationService.CurrentHwnd, x, y, isLeft: true);

        /// <summary>按键：按驱动路由（Router 记账 _heldKeys，引擎停止时 ReleaseAllHeldKeys 兜底）。</summary>
        private void SmartKeyPress(int vk, int holdMs)
            => _router.KeyPressWith(ResolveDrive(), _automationService.CurrentHwnd, vk, holdMs);

    }
}
