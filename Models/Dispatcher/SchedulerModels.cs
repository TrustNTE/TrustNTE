using System.Collections.Generic;

namespace TrustNTE.Models
{
    /// <summary>调度器配置组持久化模型：UserData\Scheduler\{组名}.json，schema v1。</summary>
    public sealed class SchedulerGroupDoc
    {
        public int Schema { get; set; } = 1;
        public string Name { get; set; } = "";
        public List<SchedulerTaskItem> Tasks { get; set; } = new();
    }

    public sealed class SchedulerTaskItem
    {
        public string Name { get; set; } = "";
        /// <summary>
        /// JsScript = Target 为 Data\Scripts 下脚本文件名；
        /// Shell    = Target 为完整 cmd 命令行；
        /// KeyMouse = 预留（宏回放，待 MacroPlayer API 接线）。
        /// </summary>
        public string Type { get; set; } = "JsScript";
        /// <summary>含义随 Type 变化（见 Type 注释）。</summary>
        public string Target { get; set; } = "";
        public int RunNum { get; set; } = 1;
        public bool Enabled { get; set; } = true;
        /// <summary>单次执行超时（秒）。0 = 不限时，靠手动停止。</summary>
        public int TimeoutSeconds { get; set; } = 0;
    }
}
