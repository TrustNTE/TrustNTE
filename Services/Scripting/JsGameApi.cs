using System;
using System.Reflection;
using System.Threading;
using OpenCvSharp;
using TrustNTE.Helpers;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Scripting
{
    /// <summary>
    /// JS↔游戏桥（宿主对象）：调度器脚本(automation)与脚本战斗策略(ctx)共用。
    /// 坐标一律 1080P 逻辑坐标（Router Logical 系方法内部做视口归一化）。
    /// 驱动由 DriveProvider 决定：调度器=跟随全局下拉；战斗策略=每帧按 CombatBackgroundMode 刷新。
    /// </summary>
    public sealed class JsGameApi
    {
        private readonly IInputRouter _router;
        private readonly IAutomationContext _auto;
        private readonly ILogService _log;
        private int _switchIndex;
        private static readonly int[] SwitchKeys = { KeyCode.D1, KeyCode.D2, KeyCode.D3, KeyCode.D4 };
        private long _lastQCastTicks;

        public JsGameApi(IInputRouter router, IAutomationContext auto, ILogService log)
        { _router = router; _auto = auto; _log = log; }

        /// <summary>驱动提供者（每帧/每次初始化设置）。</summary>
        public Func<InputDriveType> DriveProvider = () => InputDriveType.Trick;
        public InputDriveType RouterDrive => _router.CurrentDrive;

        /// <summary>★v1.4 ⏹ 停止信号：引擎每次执行脚本时接线（见 ScriptEngineService）。
        /// AttackHold 的分片等待据此立即抬起。</summary>
        public Func<bool> StopRequested = () => false;

        // ── 感知快照（战斗策略每帧刷新；调度器脚本可读但通常为默认值）──
        public volatile bool HasEnemy;
        public int EnemyX, EnemyY, EnemyW, EnemyH;
        public string EnemySource = "";
        public double EnemyConf;
        public bool QReady, QInCd;
        public double QBright;

        /// <summary>最近一次 PressQ 时刻（UTC）——策略层 Q 动画宽限判定依赖它。</summary>
        public DateTime LastQCastUtc => new DateTime(Volatile.Read(ref _lastQCastTicks), DateTimeKind.Utc);

        public void UpdateSnapshot(bool hasEnemy, Rect rect, string source, float conf,
                                   bool qReady, bool qInCd, double qBright)
        {
            HasEnemy = hasEnemy;
            EnemyX = rect.X; EnemyY = rect.Y; EnemyW = rect.Width; EnemyH = rect.Height;
            EnemySource = source ?? ""; EnemyConf = conf;
            QReady = qReady; QInCd = qInCd; QBright = qBright;
        }

        // ── 输入 ──
        private IntPtr Hwnd => _auto.CurrentHwnd;
        private bool Ready
        {
            get
            {
                if (Hwnd == IntPtr.Zero) { _log.LogWarning("[JS] 游戏窗口未绑定，本次输入跳过"); return false; }
                return true;
            }
        }

        public void Press(string key, int holdMs = 50)
        { if (Ready) _router.KeyPressWith(DriveProvider(), Hwnd, Vk(key), holdMs); }

        /// <summary>放大招：按Q并记录时刻。连招里放Q必须用它（别用 Press("Q")），否则Q动画遮挡期会误判脱战。</summary>
        public void PressQ()
        {
            if (!Ready) return;
            _router.KeyPressWith(DriveProvider(), Hwnd, KeyCode.Q, 50);
            Volatile.Write(ref _lastQCastTicks, DateTime.UtcNow.Ticks);
        }

        public void KeyDown(string key) { if (Ready) _router.KeyDown(Hwnd, Vk(key)); }
        public void KeyUp(string key) { if (Ready) _router.KeyUp(Hwnd, Vk(key)); }
        public void Click(int x, int y, bool left = true)
        { if (Ready) _router.ClickLogicalWith(DriveProvider(), Hwnd, x, y, left); }
        public void MiddleClick(int x, int y)
        { if (Ready) _router.MiddleClickLogicalWith(DriveProvider(), Hwnd, x, y); }

        /// <summary>锁定感知到的敌人中心。BOSS·LV=顶栏血条文字，点它无意义，自动跳过。</summary>
        public void LockTarget()
        {
            if (!Ready || !HasEnemy || EnemySource == "BOSS·LV") return;
            _router.MiddleClickLogicalWith(DriveProvider(), Hwnd, EnemyX + EnemyW / 2, EnemyY + EnemyH / 2);
        }

        /// <summary>中心平A（960,540）。</summary>
        public void Attack() => Click(960, 540);

        /// <summary>★v1.4 重击/蓄力：真·按住鼠标左键 holdMs 毫秒（50~30000）后抬起。
        /// 与 Attack() 的瞬时点击不同，本方法按下后持续保持。
        /// ⏹ ~50ms 内立即抬起；异常/超时 finally 必抬起；脚本结束时 ReleaseAllHeldKeys 二次兜底。</summary>
        public void AttackHold(int holdMs)
        {
            if (!Ready) return;
            holdMs = Math.Clamp(holdMs, 50, 30000);        // 1.5s / 20s 均合法
            var drive = DriveProvider();
            _router.MouseDownWith(drive, Hwnd, 960, 540);
            try
            {
                long deadline = Environment.TickCount64 + holdMs;
                while (Environment.TickCount64 < deadline)
                {
                    if (StopRequested()) return;            // ⏹：立即退出 → finally 抬起
                    Thread.Sleep(50);                       // 分片等待：20s 长按也不阻塞停止
                }
            }
            finally { _router.MouseUp(Hwnd); }
        }

        /// <summary>切换到下一位角色（D1~D4 轮转）。</summary>
        public void SwitchNext()
        {
            if (!Ready) return;
            _switchIndex = (_switchIndex + 1) % SwitchKeys.Length;
            _router.KeyPressWith(DriveProvider(), Hwnd, SwitchKeys[_switchIndex], 50);
        }

        /// <summary>直接切到第 slot 位角色（1~4）。</summary>
        public void SwitchTo(int slot)
        {
            if (!Ready) return;
            _switchIndex = Math.Clamp(slot - 1, 0, 3);
            _router.KeyPressWith(DriveProvider(), Hwnd, SwitchKeys[_switchIndex], 50);
        }

        /// <summary>★改动1(v1.3)：批量释放所有按住的键。脚本停止/超时时由引擎调用，防 WASD 卡键。</summary>
        public void ReleaseHeldKeys()
        {
            var h = Hwnd;
            if (h != IntPtr.Zero) _router.ReleaseAllHeldKeys(h);
        }

        public void Sleep(int ms) => Thread.Sleep(Math.Max(0, ms));
        public void Log(string msg) => _log.LogDebug($"[JS] {msg}");

        /// <summary>
        /// 按键名 → VK："Space""Q"/"E"/"D1"~"D4"/"Esc"/"Backspace"，
        /// 或单字符 A-Z 0-9（W/A/S/D/F 等直接可用）。
        /// </summary>
        public static int Vk(string key)
        {
            var name = (key ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("空按键名");
            if (name.Equals("Space", StringComparison.OrdinalIgnoreCase)) return 0x20;  // VK_SPACE（Windows 常量）
            var f = typeof(KeyCode).GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
            if (f != null && f.GetValue(null) is int v) return v;
            if (name.Length == 1 && char.IsLetterOrDigit(name[0])) return char.ToUpper(name[0]);
            throw new ArgumentException($"未知按键名: {key}");
        }
    }
}
