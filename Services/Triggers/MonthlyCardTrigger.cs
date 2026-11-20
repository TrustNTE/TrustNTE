using OpenCvSharp;
using System;
using TrustNTE.Helpers;
using TrustNTE.Services.Interfaces;
using TrustNTE.Services.Triggers;

namespace TrustNTE.Services.Triggers
{
    /// <summary>
    /// 月卡/环石自动领取触发器（状态机版，适配 7x24 挂机钓鱼）。
    /// 流程：检测月卡弹窗 → 点屏幕中央领取 → 等确认弹窗 → 点空白关闭 → 标记当日完成。
    /// 日切锚点：每天凌晨 4:00 重置（LastMonthlyCardClaimDate 记"游戏日"）。
    /// Guardian 类：独立任务（钓鱼等）运行期间也照常触发——弹窗盖屏会挡死任务模板匹配。
    /// </summary>
    public class MonthlyCardTrigger : ITaskTrigger
    {
        public string Name => "月卡领取";
        // ★ Guardian：钓鱼 7x24 时 Dispatch 不再静默本触发器（弹窗不点掉，钓鱼模板全瞎）
        public TriggerClass Class => TriggerClass.Guardian;
        public bool IsEnabled { get; set; } = true;

        // ===== 模板组映射（Nte_MonthlyCard.json 现有 3 组，映射错了只改这里）=====
        // 阶段1：可领取弹窗（GirlMoon/WelkinMoon 概念）；日后图集加变体往里加组名即可
        private static readonly string[] MoonGroups = { "monthly_card" };
        // 阶段2：领取后的确认弹窗（claim_gift/claim_text 概念）
        private static readonly string[] ConfirmGroups = { "huan_shi_reward" };
        // 与流程无关，命中直接忽略
        private static readonly string[] IgnoreGroups = { "Nte_Version" };

        // ===== 校准区（1080P 基准）=====
        private static readonly Rect DetectRoi = new(547, 664, 856, 364); // 弹窗出现区（沿用旧值，调试窗口校准）
        private static readonly Point CenterClickPoint = new(960, 540);   // 领取点击：屏幕中央
        private static readonly Point BlankClickPoint = new(960, 150);    // 关闭点击：弹窗外空白（若被弹窗盖住改这里）

        // ===== 节奏 =====
        private const int ScanIntervalMs = 500;    // 扫描节流（改前每帧全扫大 ROI×3模板，止血）
        private const int ConfirmPollMs = 150;     // 等确认弹窗时的轮询间隔
        private const int ConfirmTimeoutSec = 8;   // 等确认弹窗超时（按实际动画时长调）
        private const int MaxAttemptsPerDay = 3;   // 当日最多尝试轮数（防模板误匹配无限点击）
        private const int ResetHour = 4;           // 游戏日切锚点：凌晨4点

        private readonly IConfigService _configService;
        private readonly IInputRouter _router;
        private readonly ILogService _logService;
        private readonly IntPtr _hwnd;
        private readonly MonthlyCardTemplateService _templates;

        private enum ClaimPhase { Scan, WaitConfirm }
        private ClaimPhase _phase = ClaimPhase.Scan;
        private DateTime _phaseDeadline = DateTime.MinValue;
        private DateTime _lastScanUtc = DateTime.MinValue;
        private string _attemptsGameDay = string.Empty; // 尝试计数所属游戏日（跨4点清零）
        private int _attemptCount;
        private string _giveUpGameDay = string.Empty;   // 当日放弃标记（尝试用尽防刷屏）
        private string _lastHitKey = string.Empty;
        private DateTime _lastHitTime = DateTime.MinValue;

        public MonthlyCardTrigger(
            IConfigService configService, IInputRouter router,
            ILogService logService, IntPtr hwnd)
        {
            _configService = configService;
            _router = router;
            _logService = logService;
            _hwnd = hwnd;
            _templates = new MonthlyCardTemplateService(logService);
        }

        /// <summary>游戏日：当天 4:00 前属于昨天（月卡 4 点刷新）</summary>
        private static string GetGameDay(bool useLocalTime)
        {
            DateTime t = useLocalTime
                ? DateTime.Now
                : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "China Standard Time");
            return (t.TimeOfDay >= new TimeSpan(ResetHour, 0, 0)
                ? t.Date : t.Date.AddDays(-1)).ToString("yyyy-MM-dd");
        }

        public void DrawDebug(Mat drawMat)
        {
            var config = _configService.Load();
            if (!IsEnabled || !config.MonthlyCardEnabled) return;
            Cv2.Rectangle(drawMat, DetectRoi, new Scalar(0, 255, 255), 1);
            if (_lastHitTime != DateTime.MinValue && (DateTime.Now - _lastHitTime).TotalSeconds <= 1)
            {
                Cv2.PutText(drawMat, _lastHitKey, new Point(DetectRoi.X, Math.Max(12, DetectRoi.Y - 5)),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 0), 1);
            }
        }

        public void OnCapture(Mat resizedMat, double scale)
        {
            var config = _configService.Load();
            if (!config.MonthlyCardEnabled) return;

            string gameDay = GetGameDay(config.MonthlyCardUseLocalTime);

            // 跨凌晨4点：尝试计数清零、恢复扫描（这就是"4.00再次触发检查一遍"）
            if (_attemptsGameDay != gameDay)
            {
                _attemptsGameDay = gameDay;
                _attemptCount = 0;
                _phase = ClaimPhase.Scan;
            }

            // 当日已领取 / 已放弃 → 静默
            if (config.LastMonthlyCardClaimDate == gameDay) return;
            if (_giveUpGameDay == gameDay) return;

            if (DetectRoi.Bottom > resizedMat.Height || DetectRoi.Right > resizedMat.Width) return;

            if (_phase == ClaimPhase.Scan)
            {
                if ((DateTime.UtcNow - _lastScanUtc).TotalMilliseconds < ScanIntervalMs) return;
                _lastScanUtc = DateTime.UtcNow;

                var hit = _templates.MatchAnyAcrossAllGroups(resizedMat, DetectRoi, out var groupKey);
                if (hit == null) return;                       // 没弹窗 → 不执行（轻扫等待）
                if (IsIn(IgnoreGroups, groupKey)) return;

                // 重启后直接停在确认弹窗的兜底：点空白关闭并标记（与旧版"该ROI见啥点啥"行为一致）
                if (IsIn(ConfirmGroups, groupKey))
                {
                    CloseConfirmAndMark(config, gameDay, groupKey);
                    return;
                }
                if (!IsIn(MoonGroups, groupKey)) return;

                // 命中月卡弹窗 → 点屏幕中央领取 → 进入等待确认态
                _attemptCount++;
                _lastHitKey = $"{groupKey} 第{_attemptCount}次";
                _lastHitTime = DateTime.Now;
                var (px, py) = _router.ClickLogical(_hwnd, CenterClickPoint.X, CenterClickPoint.Y);
                _logService.LogInfo($"[月卡奖励] 弹窗命中（第{_attemptCount}次）{_router.ClickTag(px, py, "左键点击")} → 等待确认弹窗");

                _phase = ClaimPhase.WaitConfirm;
                _phaseDeadline = DateTime.Now.AddSeconds(ConfirmTimeoutSec);
            }
            else // WaitConfirm：轮询确认弹窗
            {
                if ((DateTime.UtcNow - _lastScanUtc).TotalMilliseconds < ConfirmPollMs) return;
                _lastScanUtc = DateTime.UtcNow;

                var hit = _templates.MatchAnyAcrossAllGroups(resizedMat, DetectRoi, out var groupKey);
                if (hit != null && IsIn(ConfirmGroups, groupKey))
                {
                    CloseConfirmAndMark(config, gameDay, groupKey);
                    return;
                }

                // 超时：确认弹窗没等到（可能已自动关闭）→ 回扫描态；次数用尽则当日放弃
                if (DateTime.Now >= _phaseDeadline)
                {
                    _logService.LogInfo($"[月卡奖励] 等待确认弹窗超时（{_attemptCount}/{MaxAttemptsPerDay}）");
                    if (_attemptCount >= MaxAttemptsPerDay)
                    {
                        _giveUpGameDay = gameDay;
                        _logService.LogWarning("[月卡奖励] 当日尝试已达上限，暂停至次日4:00（反复误匹配请校准 DetectRoi/模板）");
                    }
                    _phase = ClaimPhase.Scan;
                }
            }
        }

        private void CloseConfirmAndMark(Models.AppConfig config, string gameDay, string groupKey)
        {
            var (px, py) = _router.ClickLogical(_hwnd, BlankClickPoint.X, BlankClickPoint.Y);
            _lastHitKey = $"{groupKey} 关闭";
            _lastHitTime = DateTime.Now;
            config.LastMonthlyCardClaimDate = gameDay;
            _configService.Save(config);
            _logService.LogInfo($"[月卡奖励] 确认弹窗已关闭{_router.ClickTag(px, py, "左键点击")}，游戏日 {gameDay} 标记完成");
            _phase = ClaimPhase.Scan;
        }

        private static bool IsIn(string[] groups, string key)
        {
            foreach (var g in groups)
                if (string.Equals(g, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
