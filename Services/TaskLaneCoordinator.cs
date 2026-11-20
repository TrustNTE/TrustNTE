using System;
using System.Threading.Tasks;

namespace TrustNTE.Services
{
    /// <summary>
    /// 任务车道互斥协调器（Singleton，精简版）。
    /// 互斥判定由 TriggerManager.Dispatch 派发时实时完成
    /// （TaskRuntimeRegistry.Count + ExtraBusyCheck），本类只负责
    /// "优先启动为准"的启动前清场。
    /// </summary>
    public sealed class TaskLaneCoordinator
    {
        private SchedulerService? _scheduler;

        /// <summary>App.OnStartup 装配时调用一次（参数已改为 SchedulerService 实例）。</summary>
        public void Bind(SchedulerService scheduler)
        {
            _scheduler = scheduler;
        }

        /// <summary>
        /// "优先启动为准"：启动用户级任务 或 调度器分组 前，先有界停掉
        /// 调度器分组与其它用户级任务。守护恢复（TryStart）不走此方法。
        /// </summary>
        public async Task PreemptForUserTaskAsync()
        {
            if (_scheduler?.IsRunning == true)
            {
                try { _scheduler.Stop(); } catch { }
            }
            await TaskRuntimeRegistry.StopAllAsync(TimeSpan.FromSeconds(2));
        }
    }
}
