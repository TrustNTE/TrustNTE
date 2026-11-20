using System;
using System.IO;                // ★ 热载读策略文件时间戳
using System.Linq;
using OpenCvSharp;
using TrustNTE.Helpers;
using TrustNTE.Models;
using TrustNTE.Services;        // StrategyService
using TrustNTE.Services.Detectors;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Triggers
{
    public sealed class AutoCombatTrigger : ITaskTrigger, IDisposable
    {
        public string Name => "自动战斗";
        public bool IsEnabled { get; set; }

        private readonly IConfigService _configService;
        private readonly ILogService _logService;
        private readonly IntPtr _hwnd;
        private readonly YoloModelProvider _yoloProvider;
        private readonly QEnergyDetector _qEnergy;
        private readonly BossLvDetector _bossLv;
        private readonly CombatStrategyEngine _engine;
        private readonly IAutomationContext _autoCtx;

        // ★ 热载：记住上次生效的策略名 + 策略文件写入时间
        private string? _lastStrategyName;
        private DateTime _lastStrategyFileUtc;

        public AutoCombatTrigger(IConfigService configService, ILogService logService,
                         IInputRouter router, IntPtr hwnd, YoloModelProvider yoloProvider,
                         IAutomationContext? autoCtx = null)   // ★ 改为可选
        {
            _configService = configService;
            _logService = logService;
            _hwnd = hwnd;
            _yoloProvider = yoloProvider;
            _qEnergy = new QEnergyDetector(logService);
            _bossLv = new BossLvDetector(logService);
            _engine = new CombatStrategyEngine(router, hwnd, _qEnergy, logService);

            // ★ TriggerManager 的 ctx 没有自动化上下文可传 → 从 App 静态容器取同一个单例
            //   （AutomationService 在 DI 同时注册为 IAutomationContext，且触发器创建晚于装配，必非空）
            _autoCtx = autoCtx
                ?? (IAutomationContext)App.ServiceProvider.GetService(typeof(IAutomationContext))!;

            var cfg0 = configService.Load();
            _engine.TryLoadScriptedStrategy(cfg0, StrategyService.Load, _autoCtx); 
            _lastStrategyName = cfg0.AutoCombatStrategyName;
            _lastStrategyFileUtc = StrategyFileUtc(_lastStrategyName);
        }


        private DateTime _lastDetectTime = DateTime.MinValue;

        private int _diagFrames, _diagYolo;
        private int _rawDbg;
        private long _lastRoiKilled;
        private AppConfig? _lastConfig;
        private const int DiagReportInterval = 120;
        private sealed record DebugMark(Rect Rect, string Source);
        private volatile DebugMark? _lastDebug;

        public void OnCapture(Mat resizedMat, double scale)
        {
            if (!IsEnabled) return;
            var config = _configService.Load();
            _lastConfig = config;
            if (!config.AutoCombatEnabled) return;
            if ((DateTime.Now - _lastDetectTime).TotalMilliseconds < 100) return;
            _lastDetectTime = DateTime.Now;

            try
            {
                HotSwapStrategyIfNeeded(config);   // ★ 策略热载（改名或重新保存即生效，无需重启）
                if (config.AutoCombatUseUltimate) _qEnergy.Analyze(resizedMat);
                _bossLv.Analyze(resizedMat);
                var target = ScanTarget(resizedMat, config);
                _engine.RunFrame(target.Has, target.Rect, target.Source, target.Conf, config, DateTime.Now);
            }
            catch (Exception ex)
            {
                _logService.LogError($"[战斗] OnCapture 异常: {ex.Message}");
            }
            ReportDiag();
        }

        /// <summary>★ 策略热载：名字变化 或 策略文件被重新保存 → 重建策略。10Hz 轮询，开销可忽略。</summary>
        private void HotSwapStrategyIfNeeded(AppConfig config)
        {
            var name = config.AutoCombatStrategyName;
            var fileUtc = StrategyFileUtc(name);
            if (string.Equals(name, _lastStrategyName, StringComparison.Ordinal)
                && fileUtc == _lastStrategyFileUtc)
                return;

            _lastStrategyName = name;
            _lastStrategyFileUtc = fileUtc;

            if (string.IsNullOrWhiteSpace(name) ||
                !_engine.TryLoadScriptedStrategy(config, StrategyService.Load, _autoCtx))
            {
                _engine.ResetToDefault();
                _logService.LogInfo("[战斗] 策略已切换 → 默认反应策略");
            }
        }

        private static DateTime StrategyFileUtc(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return DateTime.MinValue;
            var p = Path.Combine(StrategyService.Dir, name + ".json");
            return File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
        }

        public void DrawDebug(Mat frame)
        {
            if (_configService.Load().AutoCombatUseUltimate)
                _qEnergy.DrawDebug(frame);

            var mark = _lastDebug;
            if (mark == null) return;
            Cv2.Rectangle(frame, mark.Rect, Scalar.Orange, 2);
            Cv2.PutText(frame, $"{mark.Source} ({mark.Rect.X + mark.Rect.Width / 2},{mark.Rect.Y + mark.Rect.Height / 2})",
                new Point(mark.Rect.X, Math.Max(14, mark.Rect.Y - 6)),
                HersheyFonts.HersheySimplex, 0.55, Scalar.Orange, 1);
        }

        private (bool Has, Rect Rect, string Source, float Conf) ScanTarget(Mat frame, AppConfig config)
        {
            if (_yoloProvider.State == YoloModelProvider.YoloLoadState.NotStarted)
                _yoloProvider.EnsureStartLoading(config.YoloUseGpu);

            var dets = _yoloProvider.Detect(frame, (float)config.AutoCombatConfidence);

#if DEBUG
            if (++_rawDbg >= 30)
            {
                _rawDbg = 0;
                var best = dets != null && dets.Count > 0
                    ? $"首个: {dets[0].ClassName} conf={dets[0].Confidence:F2}"
                    : "无";
                _logService.LogDebug($"[YOLO裸数据] dets={(dets == null ? "null(模型未就绪)" : dets.Count.ToString())} " +
                                     $"frame={frame.Width}x{frame.Height} 阈值={config.AutoCombatConfidence} | {best}");
            }
#endif

            if (dets != null)
            {
                foreach (var d in dets.OrderByDescending<CombatYoloBox, float>(x => x.Confidence))
                {
                    if (!AcceptYoloClass(d.ClassName)) continue;
                    _diagYolo++;
                    _lastDebug = new DebugMark(d.Rect, "YOLO");
                    return (true, d.Rect, "YOLO", d.Confidence);
                }
            }

            // YOLO 无检出 → 周本 BOSS 兜底：头顶 LV 模板命中即视为目标在场
            if (_bossLv.LastHit)
            {
                _lastDebug = new DebugMark(_bossLv.LastRect, "BOSS·LV");
                return (true, _bossLv.LastRect, "BOSS·LV", (float)_bossLv.LastMatch);
            }
            return (false, default, "", 0);
        }

        private static bool AcceptYoloClass(string? className)
            => string.IsNullOrWhiteSpace(className)
               || className.Equals("target", StringComparison.OrdinalIgnoreCase)
               || className.Equals("LV", StringComparison.OrdinalIgnoreCase);

        private void ReportDiag()
        {
            _diagFrames++;
            if (_diagFrames < DiagReportInterval) return;
            double total = _diagFrames;
            var roiKilled = _yoloProvider.RoiKilledTotal - _lastRoiKilled;
            _lastRoiKilled = _yoloProvider.RoiKilledTotal;
            var thr = _lastConfig?.AutoCombatConfidence.ToString("F2") ?? "?";
            _logService.LogInfo(
                $"[战斗诊断] 近{DiagReportInterval}帧 | 阈值={thr} | YOLO({_yoloProvider.StatusText}) {_diagYolo * 100 / total:F0}%" +
                $" | Q {_qEnergy.LastBright:P0} | ROI砍 {roiKilled} | BOSS {_bossLv.LastMatch:F2}");
            _diagFrames = 0; _diagYolo = 0;
        }

        public void Dispose()
        {
            _qEnergy.Dispose();
            _bossLv.Dispose();
        }
    }
}
