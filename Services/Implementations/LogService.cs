using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Interfaces.Implementations
{
    public class LogService : ILogService
    {
        public event Action<string>? OnLogReceived;
        public bool IsEnabled { get; set; } = true;

        /// <summary>开发者模式总闸：由日志窗口开关推送，App 启动时按配置初始化。</summary>
        public bool VerboseEnabled { get; set; }

        private readonly string _logDir;
        private readonly ConcurrentQueue<string> _fileQueue = new();
        private readonly object _fileLock = new();

        private const long MaxFileSize = 20 * 1024 * 1024;
        private const int MaxFileCount = 14;

        // 连发折叠：2 秒内连续相同的（级别+消息）只打第一条，收尾补 ×N 统计
        private const int RepeatWindowSeconds = 2;
        private readonly object _dedupeLock = new();
        private string? _lastKey;
        private string _lastMessage = "";
        private string _lastLevel = "INFO";
        private int _repeatCount;
        private DateTime _lastRepeatUtc;

        // 隐私脱敏：C:\Users\ 用户名打码
        private static readonly Regex UserPathRegex =
            new Regex(@"C:\\Users\\[^\\]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public LogService()
        {
            _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists(_logDir)) Directory.CreateDirectory(_logDir);

            Task.Run(ProcessFileQueueAsync);
        }

        public void Log(string message) => LogInfo(message);

        // 开发者模式开关驱动（原 Debugger.IsAttached 门控已弃用——玩家双击 exe 也必须能开）
        public void LogDebug(string message)
        {
            if (!VerboseEnabled) return;
            WriteLog("DEBUG", message, true);
        }

        public void LogInfo(string message) => WriteLog("INFO", message, true);
        public void LogWarning(string message) => WriteLog("WARN", message, true);
        public void LogError(string message) => WriteLog("ERROR", message, true);

        private void WriteLog(string level, string message, bool pushToUi)
        {
            if (!IsEnabled) return;

            // 1. 隐私脱敏
            string safeMessage = UserPathRegex.Replace(message, @"C:\Users\***");

            // 2. 连发折叠（锁内只做纯判定，不做 IO/事件回调，防重入死锁）
            string? pendingSummary = null;
            string summaryLevel = "INFO";
            bool suppressed = false;

            lock (_dedupeLock)
            {
                string key = level + "|" + safeMessage;
                DateTime nowUtc = DateTime.UtcNow;

                if (key == _lastKey && (nowUtc - _lastRepeatUtc).TotalSeconds < RepeatWindowSeconds)
                {
                    _repeatCount++;
                    _lastRepeatUtc = nowUtc;
                    suppressed = true;
                }
                else
                {
                    if (_repeatCount > 1)
                    {
                        pendingSummary = $"{_lastMessage}（连续 ×{_repeatCount}）";
                        summaryLevel = _lastLevel;
                    }

                    _lastKey = key;
                    _lastMessage = safeMessage;
                    _lastLevel = level;
                    _repeatCount = 1;
                    _lastRepeatUtc = nowUtc;
                }
            }

            if (suppressed) return;

            if (pendingSummary != null) Emit(summaryLevel, pendingSummary, pushToUi);
            Emit(level, safeMessage, pushToUi);
        }

        private void Emit(string level, string safeMessage, bool pushToUi)
        {
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            _fileQueue.Enqueue($"[{time}] [{level}] {safeMessage}");

            if (pushToUi)
                OnLogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {safeMessage}");
        }

        // 3 秒巡检：连发停止后兜底补 ×N（级别必须在锁内捕获，防止结算期被新话题覆盖）
        private void FlushExpiredRepeat()
        {
            string? summary = null;
            string level = "INFO";
            lock (_dedupeLock)
            {
                if (_repeatCount > 1 && (DateTime.UtcNow - _lastRepeatUtc).TotalSeconds >= RepeatWindowSeconds)
                {
                    summary = $"{_lastMessage}（连续 ×{_repeatCount}）";
                    level = _lastLevel;
                    _repeatCount = 1;
                }
            }
            if (summary != null) Emit(level, summary, true);
        }

        private async Task ProcessFileQueueAsync()
        {
            while (true)
            {
                try
                {
                    if (!_fileQueue.IsEmpty) WriteQueueToFile();
                    FlushExpiredRepeat();
                }
                catch { }
                await Task.Delay(3000);
            }
        }

        public void Flush()
        {
            try { WriteQueueToFile(); } catch { }
        }

        private void WriteQueueToFile()
        {
            lock (_fileLock)
            {
                string currentFile = GetCurrentLogFile();
                using (var sw = new StreamWriter(currentFile, true))
                {
                    while (_fileQueue.TryDequeue(out var line))
                        sw.WriteLine(line);
                }
            }
        }

        private string GetCurrentLogFile()
        {
            string dateStr = DateTime.Now.ToString("yyyyMMdd");
            var files = Directory.GetFiles(_logDir, $"Log_{dateStr}_*.txt").OrderByDescending(f => f).ToList();

            string? currentFile = files.FirstOrDefault();
            if (currentFile == null || new FileInfo(currentFile).Length > MaxFileSize)
            {
                currentFile = Path.Combine(_logDir, $"Log_{dateStr}_{DateTime.Now:HHmmss}.txt");
                CleanOldLogs();
            }
            return currentFile;
        }

        private void CleanOldLogs()
        {
            try
            {
                var allFiles = Directory.GetFiles(_logDir, "Log_*.txt").OrderByDescending(f => f).ToList();
                if (allFiles.Count > MaxFileCount)
                    for (int i = MaxFileCount; i < allFiles.Count; i++)
                        File.Delete(allFiles[i]);
            }
            catch { }
        }
    }
}
