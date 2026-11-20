using System;
using TrustNTE.Helpers;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Triggers
{
    /// <summary>
    /// 默认反应式策略 v2（锁定解耦版）：
    /// - Searching：见怪 → 中键锁定 → 入 Locked
    /// - Locked：锁定制游戏，攻击/技能自动朝向锁定目标 —— 打不依赖看！
    ///   平A(200ms节流)/Q/切人每帧照常输出；HasEnemy 只刷新"最后目击时间"
    /// - 脱战：最后目击超过 LostCombatSeconds → 回 Searching + 2s 重锁冷却
    /// </summary>
    public sealed class DefaultReactiveStrategy : ICombatStrategy
    {
        public string Name => "默认反应式";

        private readonly ILogService _log;
        public DefaultReactiveStrategy(ILogService log) => _log = log;

        private enum CombatPhase { Searching, Locked }
        private CombatPhase _phase = CombatPhase.Searching;
        private DateTime _lastQCast = DateTime.MinValue;
        private DateTime _lastSeenTime = DateTime.MinValue;
        private DateTime _relockCooldownUntil = DateTime.MinValue;
        private const double RelockCooldownSeconds = 2.0;

        private DateTime _lastAttackTime = DateTime.MinValue;
        private DateTime _lastSwitchTime = DateTime.MinValue;

        public void Reset()
        {
            _phase = CombatPhase.Searching;
            _lastSeenTime = DateTime.MinValue;
            _relockCooldownUntil = DateTime.MinValue;
            _lastAttackTime = DateTime.MinValue;
            _lastSwitchTime = DateTime.MinValue;
        }

        public void Decide(CombatFrameContext ctx)
        {
            switch (_phase)
            {
                case CombatPhase.Searching:
                    if (!ctx.HasEnemy || ctx.Now < _relockCooldownUntil) return;

                    // 模型识别的是怪头顶 Lv 标记 → 点其下方怪身
                    int tx = ctx.EnemyRect.X + ctx.EnemyRect.Width / 2;
                    int ty = Math.Min(ctx.EnemyRect.Y + ctx.EnemyRect.Height + 80, 1080);

                    ctx.Lock(tx, ty);
                    _log.LogInfo($"[战斗] 发现怪物({ctx.EnemySource} conf={ctx.EnemyConf:F2})，中键锁定 ({tx},{ty}) [{ctx.DriveTag}]！");

                    _phase = CombatPhase.Locked;
                    _lastSeenTime = ctx.Now;
                    _lastAttackTime = DateTime.MinValue;   // 入锁立即开打
                    _lastSwitchTime = DateTime.MinValue;
                    break;

                case CombatPhase.Locked:
                    // ① 目击只喂计时器，不再是出手的门禁
                    if (ctx.HasEnemy) _lastSeenTime = ctx.Now;

                    // ② 打不看门禁：锁定后游戏自动朝目标输出，YOLO 瞎帧照样打
                    if ((ctx.Now - _lastAttackTime).TotalMilliseconds > 200)
                    {
                        ctx.Attack();
                        _lastAttackTime = ctx.Now;
                    }

                    if (ctx.QReady && !ctx.QInCastCooldown)
                    {
                        _log.LogDebug($"[战斗] Q就绪施放 (环亮:{ctx.QBright:P0})");
                        ctx.CastQ();
                        _lastQCast = ctx.Now;
                    }

                    if ((ctx.Now - _lastSwitchTime).TotalSeconds > 3)
                    {
                        _lastSwitchTime = ctx.Now;
                        ctx.CastE();
                        ctx.SwitchNext();
                    }

                    // ③ 脱战仍由目击超时驱动（真丢了才脱，误瞎不影响输出）
                    if ((ctx.Now - _lastQCast).TotalSeconds < 3)
                    {
                        _lastSeenTime = ctx.Now;
                    }
                    else if ((ctx.Now - _lastSeenTime).TotalSeconds >= ctx.Config.LostCombatSeconds)
                    {
                        _log.LogInfo($"[战斗] 目标丢失超过{ctx.Config.LostCombatSeconds:0.#}秒，自动脱战。");
                        _phase = CombatPhase.Searching;
                        _relockCooldownUntil = ctx.Now.AddSeconds(RelockCooldownSeconds);
                    }
                    break;
            }
        }
    }
}
