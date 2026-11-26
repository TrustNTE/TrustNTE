using System;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Helpers;

namespace TrustNTE.Services.Interfaces
{
    public interface IInputRouter
    {
        /// <summary>
        /// 当前配置生效的驱动类型（用于日志展示）
        /// </summary>
        InputDriveType CurrentDrive { get; }

        // ===== 鼠标 =====
        /// <summary>
        /// 按配置驱动点击（1080P 逻辑坐标，内部归一化到当前视口）。
        /// 返回实际物理客户区坐标，供调用方打日志。
        /// </summary>
        (int Px, int Py) ClickLogical(IntPtr hwnd, int logicalX, int logicalY, bool isLeft = true);

        /// <summary>
        /// 按配置驱动点击（物理客户区坐标，不做归一化）。
        /// </summary>
        void Click(IntPtr hwnd, int clientX, int clientY, bool isLeft = true);

        /// <summary>
        /// 中键单击（战斗锁定目标）。1080P 逻辑坐标，按配置驱动路由。
        /// 返回实际物理客户区坐标，供调用方打日志。
        /// </summary>
        (int Px, int Py) MiddleClickLogical(IntPtr hwnd, int logicalX, int logicalY);

        /// <summary>
        /// 中键单击（物理客户区坐标，不做归一化）。
        /// </summary>
        void MiddleClick(IntPtr hwnd, int clientX, int clientY);

        // ===== 鼠标按住（重击/蓄力场景，  v1.4）=====
        /// <summary>
        /// 按指定驱动按下鼠标键（不抬起）。1080P 逻辑坐标，内部归一化到当前视口。
        /// 驱动与坐标进入[卡键防护]台账；脚本停止/退出时由 ReleaseAllHeldKeys 兜底抬起。
        /// 与 <see cref="MouseUp"/> 配对使用。
        /// </summary>
        void MouseDownWith(InputDriveType drive, IntPtr hwnd, int logicalX, int logicalY, bool isLeft = true);

        /// <summary>
        /// 抬起鼠标键。[卡键防护] 自动使用 MouseDown 时刻记录的驱动与坐标；未按下时幂等返回。
        /// </summary>
        void MouseUp(IntPtr hwnd, bool isLeft = true);

        // ===== 键盘 =====
        /// <summary>按配置驱动按键（按下+保持+抬起，阻塞调用线程）。</summary>
        void KeyPress(IntPtr hwnd, int virtualKey, int holdMs = 50);

        /// <summary>按配置驱动按下（不阻塞），与 KeyUp 配对（钓鱼等持续按住场景）。</summary>
        void KeyDown(IntPtr hwnd, int virtualKey);

        /// <summary>抬起。[卡键防护] 实际使用 KeyDown 时刻记录的驱动。</summary>
        void KeyUp(IntPtr hwnd, int virtualKey);

        // ===== 强制指定驱动（测试页/特殊场景，无视配置）=====
        void ClickWith(InputDriveType drive, IntPtr hwnd, int clientX, int clientY, bool isLeft = true);
        /// <summary>中键单击
        /// （物理客户区坐标，不做归一化）。
        /// 实现类已有此方法，
        /// 此处补接口声明。
        /// </summary>
        void MiddleClickWith(InputDriveType drive, IntPtr hwnd, int clientX, int clientY);

        void KeyPressWith(InputDriveType drive, IntPtr hwnd, int virtualKey, int holdMs = 50);

        // 带逻辑坐标的强制驱动方法
        void ClickLogicalWith(InputDriveType drive, IntPtr hwnd, int logicalX, int logicalY, bool isLeft = true);
        (int Px, int Py) MiddleClickLogicalWith(InputDriveType drive, IntPtr hwnd, int logicalX, int logicalY);

        // ===== 文本注入（WM_CHAR 直注，与驱动无关）=====
        Task InjectTextAsync(IntPtr hwnd, string text, int delayMs, CancellationToken ct);

        // ===== 停止/退出时的卡键释放 =====
        void ReleaseAllHeldKeys(IntPtr hwnd);
    }
}
