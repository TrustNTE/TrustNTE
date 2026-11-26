using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Triggers
{
    public class TriggerManager
    {
        private readonly List<ITaskTrigger> _triggers = new();

        public Func<bool>? ExtraBusyCheck { get; set; }

        private readonly Dictionary<string, double> _timingMs = new();
        private readonly object _timingLock = new();

        public GameLoadingTrigger? LoadingTrigger { get; private set; }
        public CombatTrigger? CombatTrigger { get; private set; }
        public AutoLootTrigger? LootTrigger { get; private set; }
        public AutoDialogueTrigger? DialogueTrigger { get; private set; }
        public AutoCombatTrigger? AutoCombatTrigger { get; private set; }
        public MonthlyCardTrigger? MonthlyCardTrigger { get; private set; }
        public DailyActiveTrigger? DailyActiveTrigger { get; private set; }

        public void InitializeAll(TriggerContext ctx)
        {
            ReleaseAllTriggers();
            _triggers.Clear();
            lock (_timingLock) { _timingMs.Clear(); }

            LoadingTrigger = new GameLoadingTrigger(
                ctx.Automation, ctx.Log, ctx.Config, ctx.Input,
                ctx.NteInteraction, ctx.Automation.Supervisor,
                ctx.Automation.GameStateTemplates, ctx.Hwnd)
            { CoordMapper = ctx.LegacyCoordMapper };

            CombatTrigger = new CombatTrigger(ctx.Log);

            LootTrigger = new AutoLootTrigger(
                ctx.Config, ctx.Router, ctx.Log, ctx.Hwnd,
                ctx.WordFTemplates, ctx.OcrRecognize)
            { CoordMapper = ctx.LegacyCoordMapper };

            DialogueTrigger = new AutoDialogueTrigger(
                ctx.Config, ctx.Router, ctx.Log, ctx.StoryTemplates, ctx.Hwnd);

            AutoCombatTrigger = new AutoCombatTrigger(
                ctx.Config, ctx.Log, ctx.Router, ctx.Hwnd, ctx.YoloProvider!);

            MonthlyCardTrigger = new MonthlyCardTrigger(
                ctx.Config, ctx.Router, ctx.Log, ctx.Hwnd);

            DailyActiveTrigger = new DailyActiveTrigger(
                ctx.Config, ctx.Router, ctx.Log, ctx.Hwnd, ctx.OcrRecognize);

            _triggers.Add(LoadingTrigger);
            _triggers.Add(CombatTrigger);
            _triggers.Add(LootTrigger);
            _triggers.Add(DialogueTrigger);
            _triggers.Add(AutoCombatTrigger);
            _triggers.Add(MonthlyCardTrigger);
            _triggers.Add(DailyActiveTrigger);
        }

        public void EnableCombatTriggers(ILogService logService)
        {
            if (CombatTrigger != null) CombatTrigger.IsEnabled = true;
            if (LootTrigger != null) LootTrigger.IsEnabled = true;
            if (DialogueTrigger != null) DialogueTrigger.IsEnabled = true;
            if (AutoCombatTrigger != null) AutoCombatTrigger.IsEnabled = true;
            if (MonthlyCardTrigger != null) MonthlyCardTrigger.IsEnabled = true;
            if (DailyActiveTrigger != null) DailyActiveTrigger.IsEnabled = true;
            logService.Log("即将跳转实时触发任务");
        }

        public void Dispatch(Mat resizedMat, double scale, CancellationToken token = default)
        {
            if (token.IsCancellationRequested) return;
            bool sequenceRunning = TaskRuntimeRegistry.Count > 0 || (ExtraBusyCheck?.Invoke() ?? false);

            foreach (var trigger in _triggers)
            {
                if (token.IsCancellationRequested) return;

                if (trigger.IsEnabled)
                {
                    if (sequenceRunning && trigger.Class == TriggerClass.Reactive) continue;

                    // 活跃度领取流程进行中，剧情触发器让路，防止误按ESC关掉领取面板
                    if (trigger is AutoDialogueTrigger
                        && DailyActiveTrigger != null
                        && DailyActiveTrigger.IsFlowActive)
                    {
                        continue;
                    }

                    var sw = Stopwatch.StartNew();
                    trigger.OnCapture(resizedMat, scale);
                    sw.Stop();
                    lock (_timingLock)
                    {
                        _timingMs.TryGetValue(trigger.Name, out var t);
                        _timingMs[trigger.Name] = t + sw.Elapsed.TotalMilliseconds;
                    }
                }
            }
        }

        public string FlushTimingReport()
        {
            lock (_timingLock)
            {
                if (_timingMs.Count == 0) return string.Empty;
                var report = string.Join(" | ", _timingMs.Select(kv => $"{kv.Key} {kv.Value:F0}ms"));
                _timingMs.Clear();
                return report;
            }
        }

        public void DrawDebug(Mat drawMat)
        {
            foreach (var trigger in _triggers)
            {
                if (trigger.IsEnabled) trigger.DrawDebug(drawMat);
            }
        }

        public void DrawCombatDebug(Mat drawMat) => AutoCombatTrigger?.DrawDebug(drawMat);

        public void DisposeAll()
        {
            ReleaseAllTriggers();
            _triggers.Clear();

            LoadingTrigger = null;
            CombatTrigger = null;
            LootTrigger = null;
            DialogueTrigger = null;
            AutoCombatTrigger = null;
            MonthlyCardTrigger = null;
            DailyActiveTrigger = null;
        }

        private void ReleaseAllTriggers()
        {
            foreach (var trigger in _triggers)
            {
                if (trigger is IDisposable disposable)
                {
                    try { disposable.Dispose(); } catch { }
                }
            }
        }
    }
}
