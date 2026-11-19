using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrustNTE.Services.Interfaces
{
    /// <summary>
    /// nte-core stdio Sidecar 客户端(协议:JSON-RPC 2.0 over NDJSON,docs/CLI_PROTOCOL_ZH.md)。
    /// 事件在后台读取线程触发,订阅方负责派发到 UI 线程(红线 2)。
    /// </summary>
    public interface IDpsCoreClient : IDisposable
    {
        /// <summary>抓包生命周期变更(值:starting / running / stopped)。</summary>
        event Action<string>? CaptureStatusChanged;

        /// <summary>event.battle.summary(4Hz),原始 JSON 透传,字段映射在 VM 层。</summary>
        event Action<JsonElement>? BattleSummaryReceived;

        /// <summary>event.core.warning / event.core.error(英文诊断,second=true 为 error)。</summary>
        event Action<string, bool>? CoreLog;

        bool IsRunning { get; }

        /// <summary>启动核心并完成 握手→detect→combat 抓包;deviceName 为空走 mode:auto(自动选网卡)。</summary>
        Task StartAsync(string coreExePath, string? deviceName, CancellationToken token);

        /// <summary>capture.stop → core.shutdown,进程退出后返回。</summary>
        Task StopAsync();

        /// <summary>battle.reset(只清战斗统计,不停抓包)。</summary>
        Task ResetBattleAsync();
    }
}
