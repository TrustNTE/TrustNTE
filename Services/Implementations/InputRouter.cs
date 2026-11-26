using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Helpers;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services
{
    /// <summary>输入路由器实现（Singleton，线程安全）。</summary>
    public sealed class InputRouter : IInputRouter
    {
        private readonly IConfigService _configService;
        private readonly IInputService _inputService;
        private readonly INteInteractionService _nte;
        private readonly ICoordinateMapper _mapper;

        /// <summary>按住中的键 → 按下时所用驱动。[卡键防护] 核心状态。</summary>
        private readonly Dictionary<int, InputDriveType> _heldKeys = new();

        /// <summary>  v1.4 按住中的鼠标键 → (按下时通道, 物理坐标)。[卡键防护] 核心状态。</summary>
        private readonly Dictionary<bool, (InputDriveType Drive, int Px, int Py)> _heldMouse = new();

        public InputRouter(
            IConfigService configService,
            IInputService inputService,
            INteInteractionService nte,
            ICoordinateMapper mapper)
        {
            _configService = configService;
            _inputService = inputService;
            _nte = nte;
            _mapper = mapper;
        }

        public InputDriveType CurrentDrive => InputDriveTypeExtensions.Parse(_configService.Load().MouseDriveType);

        // ==================== 鼠标 ====================

        public (int Px, int Py) ClickLogical(IntPtr hwnd, int logicalX, int logicalY, bool isLeft = true)
        {
            var (px, py) = _mapper.LogicalToClient(hwnd, logicalX, logicalY);
            Click(hwnd, px, py, isLeft);
            return (px, py);
        }

        public void Click(IntPtr hwnd, int clientX, int clientY, bool isLeft = true)
            => ClickWith(CurrentDrive, hwnd, clientX, clientY, isLeft);

        public void ClickWith(InputDriveType drive, IntPtr hwnd, int clientX, int clientY, bool isLeft = true)
        {
            if (hwnd == IntPtr.Zero) return;
#if DEBUG
            DebugCount(true);
#endif
            switch (drive)
            {
                case InputDriveType.Physical:
                    _inputService.SendPhysicalClickWithRestore(hwnd, clientX, clientY, isLeft);
                    break;
                case InputDriveType.Background:
                    _inputService.SendBackgroundMouseClick(hwnd, clientX, clientY, isLeft);
                    break;
                case InputDriveType.Trick:
                default:
                    _nte.NteClick(hwnd, clientX, clientY, isLeft);
                    break;
            }
        }

        //带逻辑坐标的强制左键点击
        public void ClickLogicalWith(InputDriveType drive, IntPtr hwnd, int logicalX, int logicalY, bool isLeft = true)
        {
            var (px, py) = _mapper.LogicalToClient(hwnd, logicalX, logicalY);
            ClickWith(drive, hwnd, px, py, isLeft);
        }

        // ==================== 中键（战斗锁定目标） ====================

        public (int Px, int Py) MiddleClickLogical(IntPtr hwnd, int logicalX, int logicalY)
        {
            var (px, py) = _mapper.LogicalToClient(hwnd, logicalX, logicalY);
            MiddleClick(hwnd, px, py);
            return (px, py);
        }

        public void MiddleClick(IntPtr hwnd, int clientX, int clientY)
            => MiddleClickWith(CurrentDrive, hwnd, clientX, clientY);

        public void MiddleClickWith(InputDriveType drive, IntPtr hwnd, int clientX, int clientY)
        {
            if (hwnd == IntPtr.Zero) return;
#if DEBUG
            DebugCount(true);
#endif
            switch (drive)
            {
                case InputDriveType.Physical:
                    _inputService.SendPhysicalMiddleClickWithRestore(hwnd, clientX, clientY);
                    break;
                case InputDriveType.Background:
                    _inputService.SendMiddleClick(hwnd, clientX, clientY);
                    break;
                case InputDriveType.Trick:
                default:
                    _nte.NteMiddleClick(hwnd, clientX, clientY);
                    break;
            }
        }

        // 带逻辑坐标的强制中键点击
        public (int Px, int Py) MiddleClickLogicalWith(InputDriveType drive, IntPtr hwnd, int logicalX, int logicalY)
        {
            var (px, py) = _mapper.LogicalToClient(hwnd, logicalX, logicalY);
            MiddleClickWith(drive, hwnd, px, py);
            return (px, py);
        }

        // ====================   v1.4 鼠标按住（重击/蓄力） ====================

        /// <summary>
        ///   v1.4.2 按下鼠标（长按起点）。恢复按驱动路由（后台原语已根治：补齐 SetWindowPos/MOVE/Sleep 前置）：
        ///   · Background 左键：原生 SendLeftMouseDown（后台通道原生处理长按）；
        ///   · Background 右键：后台原语仅覆盖左键，降级走 Trick；
        ///   · Physical：无独立 Down/Up 原语，降级走 Trick（完整物理点击仍走 ClickWith 原路径）；
        ///   · Trick：原生 NteMouseDown（HoldTest 已实测三条全灵）。
        /// 台账一律记录"实际发送通道"（effective），MouseUp/ReleaseAllHeldKeys 自动对齐，绝无 Down/Up 错配。
        /// </summary>
        public void MouseDownWith(InputDriveType drive, IntPtr hwnd, int logicalX, int logicalY, bool isLeft = true)
        {
            if (hwnd == IntPtr.Zero) return;
            var (px, py) = _mapper.LogicalToClient(hwnd, logicalX, logicalY);

            lock (_heldMouse)
            {
                // 防双按：同键已用其他通道按下时，先按旧通道抬起（对齐 KeyDownWith 模式）
                if (_heldMouse.TryGetValue(isLeft, out var old) && old.Drive != drive)
                {
                    MouseUpSend(hwnd, isLeft, old);
                    _heldMouse.Remove(isLeft);
                }
            }

            var effective = drive;
            switch (drive)   //   先发送，成功后才入台账：发送失败绝不残留"按下未抬起"
            {
                case InputDriveType.Physical:
                    // 物理驱动无独立 Down/Up 原语 → 长按降级走 Trick
                    _nte.NteMouseDown(hwnd, px, py, isLeft);
                    effective = InputDriveType.Trick;
                    break;
                case InputDriveType.Background:
                    if (isLeft)
                    {
                        //   v1.4.2 原生路径：SendLeftMouseDown 已补齐前置，与完整点击同权重
                        _inputService.SendLeftMouseDown(hwnd, px, py);
                    }
                    else
                    {
                        // 后台按下原语仅左键 → 右键长按降级走 Trick
                        _nte.NteMouseDown(hwnd, px, py, isLeft);
                        effective = InputDriveType.Trick;
                    }
                    break;
                case InputDriveType.Trick:
                default:
                    _nte.NteMouseDown(hwnd, px, py, isLeft);
                    break;
            }
#if DEBUG
            DebugCount(true);
#endif
            lock (_heldMouse)
                _heldMouse[isLeft] = (effective, px, py);   //   记录实际通道，Up 自动对齐
        }

        /// <summary>抬起鼠标（长按终点）。按台账记录的通道与坐标发送；未按下时幂等返回。</summary>
        public void MouseUp(IntPtr hwnd, bool isLeft = true)
        {
            (InputDriveType Drive, int Px, int Py) state;
            lock (_heldMouse)
            {
                if (!_heldMouse.Remove(isLeft, out state)) return;   // 无台账=本就未按下，幂等返回
            }
            MouseUpSend(hwnd, isLeft, state);
        }

        /// <summary>  v1.4 按台账记录的通道发送抬起（私有，调用方持台账数据）。</summary>
        private void MouseUpSend(IntPtr hwnd, bool isLeft, (InputDriveType Drive, int Px, int Py) state)
        {
            switch (state.Drive)
            {
                case InputDriveType.Physical:
                    return;   // Physical 从不入台账（Down 已降级），防御性保留
                case InputDriveType.Background:
                    if (hwnd == IntPtr.Zero) return;
                    _inputService.SendLeftMouseUp(hwnd, state.Px, state.Py);
                    break;
                case InputDriveType.Trick:
                default:
                    if (hwnd == IntPtr.Zero) return;
                    _nte.NteMouseUp(hwnd, state.Px, state.Py, isLeft);
                    break;
            }
        }

        // ==================== 键盘 ====================

        public void KeyPress(IntPtr hwnd, int virtualKey, int holdMs = 50)
        {
            if (hwnd == IntPtr.Zero) return;
            KeyDown(hwnd, virtualKey);
            Thread.Sleep(holdMs);
            KeyUp(hwnd, virtualKey);
        }

        public void KeyDown(IntPtr hwnd, int virtualKey) => KeyDownWith(CurrentDrive, hwnd, virtualKey);

        private void KeyDownWith(InputDriveType drive, IntPtr hwnd, int virtualKey)
        {
            if (hwnd == IntPtr.Zero) return;

            lock (_heldKeys)
            {
                // 防止多线程并发按下同一个键但驱动不同，避免双通道按下
                if (_heldKeys.TryGetValue(virtualKey, out var oldDrive) && oldDrive != drive)
                {
                    KeyUpWith(hwnd, virtualKey, oldDrive);
                }
                _heldKeys[virtualKey] = drive;
            }
#if DEBUG
            DebugCount(false);
#endif
            switch (drive)
            {
                case InputDriveType.Physical:
                    Win32Input.KeyDown(virtualKey);
                    break;
                case InputDriveType.Background:
                    _inputService.SendKey(hwnd, virtualKey, true);
                    break;
                case InputDriveType.Trick:
                default:
                    _nte.NteKeyDown(hwnd, virtualKey);
                    break;
            }
        }

        public void KeyUp(IntPtr hwnd, int virtualKey)
        {
            InputDriveType drive;
            lock (_heldKeys)
            {
                if (!_heldKeys.Remove(virtualKey, out drive)) drive = CurrentDrive;
            }
            KeyUpWith(hwnd, virtualKey, drive);
        }

        private void KeyUpWith(IntPtr hwnd, int virtualKey, InputDriveType drive)
        {
            switch (drive)
            {
                case InputDriveType.Physical:
                    // 物理键是全局 SendInput，与目标窗口无关。
                    // 即使 hwnd 已失效（引擎已停止）也必须补发抬起，否则系统级卡键。
                    Win32Input.KeyUp(virtualKey);
                    break;
                case InputDriveType.Background:
                    if (hwnd == IntPtr.Zero) return;
                    _inputService.SendKey(hwnd, virtualKey, false);
                    break;
                case InputDriveType.Trick:
                default:
                    if (hwnd == IntPtr.Zero) return;
                    _nte.NteKeyUp(hwnd, virtualKey);
                    break;
            }
        }

        public void KeyPressWith(InputDriveType drive, IntPtr hwnd, int virtualKey, int holdMs = 50)
        {
            if (hwnd == IntPtr.Zero) return;
            KeyDownWith(drive, hwnd, virtualKey);
            Thread.Sleep(holdMs);
            KeyUp(hwnd, virtualKey);
        }

        // ==================== 卡键防护：一次性释放所有按住的键 ====================

        public void ReleaseAllHeldKeys(IntPtr hwnd)
        {
            lock (_heldKeys)
            {
                foreach (var kv in _heldKeys)
                {
                    try { KeyUpWith(hwnd, kv.Key, kv.Value); } catch { }
                }
                _heldKeys.Clear();
            }
            lock (_heldMouse)   //   v1.4 鼠标台账同批释放，⏹/退出时按住的左键必抬起
            {
                foreach (var kv in _heldMouse)
                {
                    try { MouseUpSend(hwnd, kv.Key, kv.Value); } catch { }
                }
                _heldMouse.Clear();
            }
        }

        // ==================== 文本注入 ====================

        public Task InjectTextAsync(IntPtr hwnd, string text, int delayMs, CancellationToken ct)
            => NteTextInjector.InjectTextAsync(hwnd, text, delayMs, ct);

        // ==================== [DEBUG] 内部吞吐量统计 ====================
#if DEBUG
        private int _dbgClicks, _dbgKeys;
        private long _lastDbgFlushMs = Environment.TickCount64;

        private void DebugCount(bool isClick)
        {
            if (isClick) Interlocked.Increment(ref _dbgClicks);
            else Interlocked.Increment(ref _dbgKeys);

            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastDbgFlushMs);
            if (now - last >= 5000 && Interlocked.CompareExchange(ref _lastDbgFlushMs, now, last) == last)
            {
                int c = Interlocked.Exchange(ref _dbgClicks, 0);
                int k = Interlocked.Exchange(ref _dbgKeys, 0);
                System.Diagnostics.Debug.WriteLine($"[InputRouter] 5s 窗口: 点击×{c} 按键×{k} 驱动={CurrentDrive.ToConfigString()}");
            }
        }
#endif
    }
}
