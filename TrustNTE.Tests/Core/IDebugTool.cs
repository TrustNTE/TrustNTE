using TrustNTE.Tests.DebugTools;

namespace TrustNTE.Tests.Core
{
    /// <summary>
    /// 所有调试工具的统一契约。
    /// Program 会先启动 TestCaptureDrone 再调用 Run，工具内部不用管截图线程生命周期。
    /// </summary>
    public interface IDebugTool
    {
        /// <summary>菜单显示名</summary>
        string Name { get; }

        /// <summary>一句话说明（显示在菜单名右侧）</summary>
        string Description { get; }

        /// <summary>工具入口（内部可以再有自己的子菜单）</summary>
        void Run(TestCaptureDrone drone);
    }
}
