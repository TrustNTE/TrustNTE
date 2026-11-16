using OpenCvSharp;
using System;
using System.Linq;              // ★ 空队伍守卫需要
using TrustNTE.Helpers;
using TrustNTE.Models;
using TrustNTE.Services.Combat;
using TrustNTE.Services.Interfaces;
using TrustNTE.Services.Scripting;

namespace TrustNTE.Services.Triggers
{
    /// <summary>
    /// 战斗策略引擎：感知由触发器提供，决策委托 ICombatStrategy，指令统一走 Router。
    /// 驱动选择：CombatBackgroundMode=true 强制后台（红线①），DriveTag 反映实际驱动（红线②防撒令）。
    /// </summary>
    public sealed class CombatStrategyEngine
    {
        private readonly IInputRouter _router;
        private readonly IntPtr _hwnd;
        private readonly QEnergyDetector _qEnergy;
        private readonly ILogService _log;

        private ICombatStrategy _strategy;

        private int _currentCharIndex;
        private static readonly int[] SwitchKeys = { KeyCode.D1, KeyCode.D2, KeyCode.D3, KeyCode.D4 };

        public string StrategyName => _strategy.Name;

        public CombatStrategyEngine(IInputRouter router, IntPtr hwnd,
                                    QEnergyDetector qEnergy, ILogService log)
        {
            _router = router;
            _hwnd = hwnd;
            _qEnergy = qEnergy;
            _log = log;
            _strategy = new DefaultReactiveStrategy(log);
        }

        public void SetStrategy(ICombatStrategy strategy)
        {
            // 旧策略若是脚本策略，必须释放其 V8 引擎与连招任务，否则切策略/重启引擎时泄漏
            if (!ReferenceEquals(_strategy, strategy) && _strategy is IDisposable d)
                d.Dispose();

            _strategy = strategy;
            strategy.Reset();
            _currentCharIndex = 0;
        }

        /// <summary>
        /// 从配置加载脚本策略。成功返回 true；无策略名/加载失败/队伍为空时返回 false，保持原策略。
        /// </summary>
        public bool TryLoadScriptedStrategy(
            AppConfig config,
            Func<string, StrategyDocument?> loadDoc,
            IAutomationContext autoCtx)
        {
            var name = config?.AutoCombatStrategyName;
            if (string.IsNullOrWhiteSpace(name)) return false;

            StrategyDocument? doc;
            try
            {
                doc = loadDoc?.Invoke(name);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"[战斗] 策略「{name}」加载异常: {ex.Message}，回退默认反应策略");
                return false;
            }

            // ★ Team 默认含 4 个 null 槽，Count==0 永远为假 → 必须按内容判空
            if (doc == null || doc.Team.All(t => string.IsNullOrWhiteSpace(t)))
            {
                _log.LogWarning($"[战斗] 策略「{name}」加载失败/无队伍，回退默认反应策略");
                return false;
            }

            SetStrategy(new ScriptedStrategy(doc, _router, autoCtx, _log));
            _log.LogInfo($"[战斗] 脚本策略「{name}」已接管（队伍 {string.Join("/", doc.Team)}）");
            return true;
        }

        /// <summary>★ 新增：切回默认反应策略（策略被删/加载失败/策略名清空时调用）。</summary>
        public void ResetToDefault() => SetStrategy(new DefaultReactiveStrategy(_log));

        /// <summary>实际驱动：后台战斗配置开启 → 强制后台；否则跟随全局。</summary>
        private InputDriveType DriveFor(AppConfig c)
            => c.CombatBackgroundMode ? InputDriveType.Background : _router.CurrentDrive;

        /// <summary>每帧入口。感知参数由触发器填充（触发器已做 100ms 节流）。</summary>
        public void RunFrame(bool hasEnemy, Rect enemyRect, string source, float enemyConf,
                             AppConfig config, DateTime now)
        {
            var drive = DriveFor(config);
            var ctx = new CombatFrameContext
            {
                HasEnemy = hasEnemy,
                EnemyRect = enemyRect,
                EnemySource = source,
                EnemyConf = enemyConf,
                Config = config,
                Now = now,
                QReady = config.AutoCombatUseUltimate && _qEnergy.IsReady,
                QInCastCooldown = _qEnergy.InCastCooldown,
                QBright = _qEnergy.LastBright,
                DriveTag = drive.ToConfigString(),
            };
            ctx.Bind(
                lockCmd: (x, y) =>
                {
                    if (source != "BOSS·LV")
                        _router.MiddleClickLogicalWith(drive, _hwnd, x, y);
                },
                attackCmd: () => _router.ClickLogicalWith(drive, _hwnd, 960, 540),
                castQCmd: () => { _router.KeyPressWith(drive, _hwnd, KeyCode.Q, 50); _qEnergy.NotifyCast(); },
                castECmd: () => _router.KeyPressWith(drive, _hwnd, KeyCode.E, 50),
                switchNextCmd: () =>
                {
                    _currentCharIndex = (_currentCharIndex + 1) % SwitchKeys.Length;
                    _router.KeyPressWith(drive, _hwnd, SwitchKeys[_currentCharIndex], 50);
                });

            _strategy.Decide(ctx);
        }
    }
}
