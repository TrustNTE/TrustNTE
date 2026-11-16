using System;
using OpenCvSharp;
using TrustNTE.Models;

namespace TrustNTE.Services.Triggers
{
    /// <summary>
    /// 战斗策略接口（P1 策略宿主架构）：
    /// - 引擎每帧构造感知快照并调用 Decide 一次（引擎已做 100ms 节流）
    /// - 策略只读感知、发指令，禁止自取 Router/窗口句柄（红线1）
    /// - P2：JS 策略（Jint）包装成同接口；P3：UI 从 Strategies/ 目录热加载
    /// </summary>
    public interface ICombatStrategy
    {
        /// <summary>策略名（日志/UI 用）</summary>
        string Name { get; }

        /// <summary>每帧决策。内部不要 sleep/阻塞。</summary>
        void Decide(CombatFrameContext ctx);

        /// <summary>策略切换/重置时清空内部状态。</summary>
        void Reset();
    }

    /// <summary>
    /// 每帧感知快照 + 指令发射器。感知字段由引擎填充（只读），
    /// 指令方法由引擎绑定到真实输入路由。
    /// </summary>
    public sealed class CombatFrameContext
    {
        // ===== 感知（引擎填充，策略只读）=====
        /// <summary>本帧是否检出敌人（已过置信度阈值）</summary>
        public bool HasEnemy { get; init; }
        /// <summary>敌人框（1080P 逻辑坐标；识别的是头顶 Lv 牌）</summary>
        public Rect EnemyRect { get; init; }
        /// <summary>检出来源（"YOLO" 等）</summary>
        public string EnemySource { get; init; } = "";

        /// <summary>检出置信度 0~1（取证：区分真怪/误检）</summary>
        public float EnemyConf { get; init; }

        /// <summary>Q 是否就绪（UseUltimate 关闭时恒为 false）</summary>
        public bool QReady { get; init; }
        /// <summary>Q 施放冷却中（防读数回落期重复按）</summary>
        public bool QInCastCooldown { get; init; }
        /// <summary>Q 能量条亮度 0~1</summary>
        public double QBright { get; init; }
        /// <summary>当前配置（策略可读开关，输入一律走指令）</summary>
        public AppConfig Config { get; init; } = null!;
        /// <summary>本帧时间戳</summary>
        public DateTime Now { get; init; }
        /// <summary>当前实际驱动标签（日志用，显示真实驱动防撒谎）</summary>
        public string DriveTag { get; init; } = "";

        // ===== 指令（引擎绑定，策略调用）=====
        private Action<int, int>? _lockCmd;
        private Action? _attackCmd;
        private Action? _castQCmd;
        private Action? _castECmd;
        private Action? _switchNextCmd;

        internal void Bind(Action<int, int> lockCmd, Action attackCmd,
                           Action castQCmd, Action castECmd, Action switchNextCmd)
        {
            _lockCmd = lockCmd; _attackCmd = attackCmd;
            _castQCmd = castQCmd; _castECmd = castECmd; _switchNextCmd = switchNextCmd;
        }

        /// <summary>中键锁定目标（传 Lv 牌下方怪身位置的 1080P 逻辑坐标）</summary>
        public void Lock(int logicalX, int logicalY) => _lockCmd!(logicalX, logicalY);
        /// <summary>左键平A（固定屏幕中心 960,540）</summary>
        public void Attack() => _attackCmd!();
        /// <summary>施放 Q（引擎内部自动 NotifyCast 记录冷却）</summary>
        public void CastQ() => _castQCmd!();
        /// <summary>按 E（战术技能）</summary>
        public void CastE() => _castECmd!();
        /// <summary>切换下一个角色（数字键 1→2→3→4 循环，引擎管理序号）</summary>
        public void SwitchNext() => _switchNextCmd!();
    }
}
