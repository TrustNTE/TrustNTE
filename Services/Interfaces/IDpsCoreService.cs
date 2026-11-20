using TrustNTE.Models.Dps;

namespace TrustNTE.Services.Interfaces
{
    /// <summary>
    /// nte-core stdio Sidecar 接口。事件均在线程池读取线程触发，UI 订阅方必须经 IDispatcherService 派发。
    /// </summary>
    public interface IDpsCoreService
    {
        bool IsCoreRunning { get; }
        bool IsCaptureRunning { get; }
        string CoreVersion { get; }

        event Action<DpsBattleSummaryDto>? BattleSummaryReceived;
        event Action<DpsCaptureStatusDto>? CaptureStatusReceived;
        event Action<bool>? CoreStateChanged;
        event Action<string, string>? DiagnosticReceived;

        Task<DpsCaptureDetectResult> DetectAsync(CancellationToken cancellationToken);
        Task StartCaptureAsync(string? deviceName, CancellationToken cancellationToken);
        Task StopCaptureAsync(CancellationToken cancellationToken);
        Task ResetBattleAsync(CancellationToken cancellationToken);
        Task<DpsBattleTimelineDto?> GetTimelineAsync(CancellationToken cancellationToken);
        Task<DpsBattleAxisPageDto?> GetAxisPageAsync(string? cursor, int limit, CancellationToken cancellationToken);
        Task ShutdownAsync(CancellationToken cancellationToken);
    }
}
