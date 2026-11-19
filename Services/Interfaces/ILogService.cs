using System;

namespace TrustNTE.Services.Interfaces
{
    public interface ILogService
    {
        event Action<string>? OnLogReceived;
        bool IsEnabled { get; set; }

        /// <summary>开发者模式总闸：true 时 LogDebug 进入管线并推送 UI。启动时按配置初始化。</summary>
        bool VerboseEnabled { get; set; }

        void Log(string message);
        void LogDebug(string message);
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
    }
}
