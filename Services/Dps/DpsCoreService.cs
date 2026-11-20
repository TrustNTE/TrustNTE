using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrustNTE.Models.Dps;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Dps
{
    /// <summary>
    /// nte-core JSON-RPC 2.0 over NDJSON stdio 客户端。
    /// 一个独占 stdout reader 同时路由响应和事件，所有请求经信号量串行写入。
    /// </summary>
    public sealed class DpsCoreService : IDpsCoreService, IDisposable
    {
        private const string CoreExeFile = "Nte-Dps.exe";
        private const int ProtocolVersion = 1;
        private const int MinAxisLimit = 1;
        private const int MaxAxisLimit = 500;

        // 系统健壮性边界：不是业务识别参数。停止需 join 抓包线程，因此使用更长超时。
        private const int RequestTimeoutMs = 10_000;
        private const int StopRequestTimeoutMs = 20_000;
        private const int ProcessExitTimeoutMs = 3_000;
        private const int DisposeTimeoutMs = 5_000;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ILogService _logService;
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

        private Process? _process;
        private CancellationTokenSource? _processCts;
        private Task? _stdoutTask;
        private Task? _stderrTask;
        private long _nextRequestId;
        private bool _handshaken;
        private bool _disposed;
        private volatile bool _coreRunning;
        private volatile bool _captureRunning;

        public bool IsCoreRunning => _coreRunning;
        public bool IsCaptureRunning => _captureRunning;
        public string CoreVersion { get; private set; } = "";

        public event Action<DpsBattleSummaryDto>? BattleSummaryReceived;
        public event Action<DpsCaptureStatusDto>? CaptureStatusReceived;
        public event Action<bool>? CoreStateChanged;
        public event Action<string, string>? DiagnosticReceived;

        public DpsCoreService(ILogService logService)
        {
            _logService = logService;
        }

        public async Task<DpsCaptureDetectResult> DetectAsync(CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                await EnsureHandshakeLockedAsync(cancellationToken);
                var result = await RequestLockedAsync(
                    "capture.detect", new { }, cancellationToken, RequestTimeoutMs);
                return DeserializeResult<DpsCaptureDetectResult>(result)
                       ?? throw new InvalidDataException("capture.detect 返回了空结果。");
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public async Task StartCaptureAsync(string? deviceName, CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                await EnsureHandshakeLockedAsync(cancellationToken);

                object device;
                if (string.IsNullOrWhiteSpace(deviceName))
                    device = new { mode = "auto" };
                else
                    device = new { mode = "name", name = deviceName };

                var result = await RequestLockedAsync(
                    "capture.start",
                    new
                    {
                        profile = "combat",
                        device,
                        include_incoming = true,
                        server_damage_calibration = true,
                        raw_capture = "disabled"
                    },
                    cancellationToken,
                    RequestTimeoutMs);

                var started = DeserializeResult<CaptureStartResult>(result);
                if (started == null || string.IsNullOrWhiteSpace(started.OperationId))
                    throw new InvalidDataException("capture.start 未返回有效 operation_id。");

                _captureRunning = true;
                _logService.LogInfo("[DPS] 真实抓包已启动，原始 PCAP 落盘已关闭。");
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public async Task StopCaptureAsync(CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                if (!_captureRunning) return;
                await EnsureHandshakeLockedAsync(cancellationToken);

                try
                {
                    await RequestLockedAsync(
                        "capture.stop", new { }, cancellationToken, StopRequestTimeoutMs);
                }
                catch (DpsCoreException ex) when (ex.DomainCode == "CAPTURE_NOT_RUNNING")
                {
                    // Core 已先发出 stopped；本地状态与 Core 对齐即可。
                }
                finally
                {
                    _captureRunning = false;
                }

                _logService.LogInfo("[DPS] 抓包已停止，Core 保留用于最终摘要和战报查询。");
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public async Task ResetBattleAsync(CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                await EnsureHandshakeLockedAsync(cancellationToken);
                await RequestLockedAsync("battle.reset", new { }, cancellationToken, RequestTimeoutMs);
                _logService.LogInfo("[DPS] Core 战斗数据已重置。");
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public async Task<DpsBattleTimelineDto?> GetTimelineAsync(CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                await EnsureHandshakeLockedAsync(cancellationToken);
                var result = await RequestLockedAsync(
                    "battle.get_timeline",
                    new
                    {
                        battle_record_id = (string?)null,
                        scope = "all",
                        bucket_seconds = 1.0,
                        subtract_time_stop = true
                    },
                    cancellationToken,
                    RequestTimeoutMs);
                return DeserializeResult<DpsBattleTimelineDto>(result);
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public async Task<DpsBattleAxisPageDto?> GetAxisPageAsync(
            string? cursor, int limit, CancellationToken cancellationToken)
        {
            if (limit is < MinAxisLimit or > MaxAxisLimit)
                throw new ArgumentOutOfRangeException(nameof(limit), "Core 逐击分页 limit 必须在 1～500。");

            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                await EnsureHandshakeLockedAsync(cancellationToken);
                var result = await RequestLockedAsync(
                    "battle.get_axis",
                    new
                    {
                        battle_record_id = (string?)null,
                        cursor,
                        limit
                    },
                    cancellationToken,
                    RequestTimeoutMs);
                return DeserializeResult<DpsBattleAxisPageDto>(result);
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                var process = _process;
                if (process == null)
                {
                    CleanupProcessLocked();
                    return;
                }

                try
                {
                    if (_captureRunning && _handshaken && _coreRunning)
                    {
                        try
                        {
                            await RequestLockedAsync(
                                "capture.stop", new { }, cancellationToken, StopRequestTimeoutMs);
                        }
                        catch (DpsCoreException ex) when (ex.DomainCode == "CAPTURE_NOT_RUNNING")
                        {
                            // 已停止，无需再处理。
                        }
                    }

                    if (_coreRunning && !process.HasExited)
                    {
                        await RequestLockedAsync(
                            "core.shutdown", new { }, cancellationToken, RequestTimeoutMs);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 关窗/退出取消后立即走有界终止和句柄清理。
                }
                catch (Exception ex)
                {
                    _logService.LogDebug($"[DPS] Core 正常关闭未完成，将终止进程：{ex.Message}");
                }
                finally
                {
                    _captureRunning = false;
                    await TerminateAndCleanupLockedAsync(process, cancellationToken);
                }
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private async Task EnsureHandshakeLockedAsync(CancellationToken cancellationToken)
        {
            EnsureProcessLocked();
            if (_handshaken) return;

            var result = await RequestLockedAsync(
                "core.hello",
                new
                {
                    client_name = "TrustNTE",
                    client_version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
                    protocol_min = ProtocolVersion,
                    protocol_max = ProtocolVersion
                },
                cancellationToken,
                RequestTimeoutMs);

            var hello = DeserializeResult<HelloResult>(result)
                        ?? throw new InvalidDataException("core.hello 返回了空结果。");
            if (hello.ProtocolVersion != ProtocolVersion)
            {
                throw new DpsCoreException(
                    "PROTOCOL_VERSION_MISMATCH",
                    $"Core 协议版本 {hello.ProtocolVersion} 与客户端支持的版本 {ProtocolVersion} 不兼容。");
            }

            string[] requiredCapabilities =
            {
                "capture", "battle_summary", "battle_axis_v1", "battle_timeline_v1"
            };
            var missing = requiredCapabilities.FirstOrDefault(
                capability => !hello.Capabilities.Contains(capability, StringComparer.Ordinal));
            if (missing != null)
            {
                throw new DpsCoreException(
                    "CAPABILITY_NOT_SUPPORTED",
                    $"当前 Core 缺少必需能力：{missing}。请使用配套的 Nte-Dps.exe。");
            }

            CoreVersion = hello.CoreVersion;
            _handshaken = true;
            _logService.LogInfo(
                $"[DPS] Core 握手成功，版本 {hello.CoreVersion}，协议 v{hello.ProtocolVersion}。");
        }

        private void EnsureProcessLocked()
        {
            if (_process != null && _coreRunning)
            {
                try
                {
                    if (!_process.HasExited) return;
                }
                catch (InvalidOperationException)
                {
                    // 进程句柄已失效，下面按旧实例清理并重新启动。
                }
            }

            if (_process != null) CleanupProcessLocked();

            var exePath = ResolveCorePath();
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("serve");
            psi.ArgumentList.Add("--stdio");

            var process = new Process { StartInfo = psi };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Nte-Dps.exe 未能创建进程。");
            }
            catch
            {
                process.Dispose();
                throw;
            }

            _processCts = new CancellationTokenSource();
            _process = process;
            _handshaken = false;
            _captureRunning = false;
            _coreRunning = true;
            RaiseCoreStateChanged(true);

            // 两个读取循环内部均观察并记录异常，不产生未观察 Task。
            _stdoutTask = ReadStdoutLoopAsync(process, _processCts.Token);
            _stderrTask = ReadStderrLoopAsync(process, _processCts.Token);
            _logService.LogInfo("[DPS] Nte-Dps.exe serve --stdio 已启动。");
        }

        private static string ResolveCorePath()
        {
            var baseDirectory = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDirectory, CoreExeFile),
                Path.Combine(baseDirectory, "Assets", "Nte_Dps", CoreExeFile)
            };
            var existing = candidates.FirstOrDefault(File.Exists);
            if (existing != null) return existing;

            throw new FileNotFoundException(
                "未找到 Nte-Dps.exe。请放在主程序同目录，或 Assets\\Nte_Dps\\ 下。");
        }

        private async Task<JsonElement> RequestLockedAsync(
            string method, object parameters, CancellationToken cancellationToken, int timeoutMs)
        {
            var process = _process;
            if (process == null || !_coreRunning || process.HasExited)
                throw new InvalidOperationException("DPS Core 进程当前不可用。");

            var requestId = Interlocked.Increment(ref _nextRequestId)
                .ToString(CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<JsonElement>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(requestId, completion))
                throw new InvalidOperationException("无法登记 DPS Core 请求。");

            try
            {
                var json = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = requestId,
                    method,
                    @params = parameters
                });

                await process.StandardInput.WriteAsync((json + "\n").AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeoutMs);
                try
                {
                    return await completion.Task.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"DPS Core 请求 {method} 超过 {timeoutMs}ms 未响应。");
                }
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
            }
        }

        private async Task ReadStdoutLoopAsync(Process process, CancellationToken cancellationToken)
        {
            Exception? readerFailure = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        RouteStdoutMessage(document.RootElement);
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning($"[DPS] Core stdout 消息无法处理：{ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 主动关闭。
            }
            catch (Exception ex)
            {
                readerFailure = ex;
                if (!cancellationToken.IsCancellationRequested)
                    _logService.LogWarning($"[DPS] Core stdout 读取结束：{ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_process, process))
                {
                    _captureRunning = false;
                    _coreRunning = false;
                    _handshaken = false;
                    CoreVersion = "";
                    RaiseCoreStateChanged(false);
                    FailPending(new IOException("DPS Core stdout 已关闭。", readerFailure));
                }
            }
        }

        private async Task ReadStderrLoopAsync(Process process, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(cancellationToken);
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                        _logService.LogDebug($"[DPS] Core 诊断：{line}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 主动关闭。
            }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    _logService.LogDebug($"[DPS] Core stderr 读取结束：{ex.Message}");
            }
        }

        private void RouteStdoutMessage(JsonElement root)
        {
            if (root.TryGetProperty("id", out var idElement))
            {
                var requestId = idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : idElement.GetRawText();
                if (requestId == null || !_pending.TryRemove(requestId, out var completion)) return;

                if (root.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(CreateRpcException(error));
                    return;
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    completion.TrySetException(new InvalidDataException("Core 响应缺少 result。"));
                    return;
                }

                completion.TrySetResult(result.Clone());
                return;
            }

            if (!root.TryGetProperty("method", out var methodElement)) return;
            var methodName = methodElement.GetString();
            if (string.IsNullOrWhiteSpace(methodName) || !methodName.StartsWith("event.", StringComparison.Ordinal))
                return;

            var eventParams = root.TryGetProperty("params", out var parameters)
                ? parameters
                : default;
            RouteCoreEvent(methodName, eventParams);
        }

        private void RouteCoreEvent(string method, JsonElement parameters)
        {
            switch (method)
            {
                case "event.battle.summary":
                {
                    var summary = parameters.Deserialize<DpsBattleSummaryDto>(JsonOptions);
                    if (summary != null) BattleSummaryReceived?.Invoke(summary);
                    break;
                }
                case "event.capture.status":
                {
                    var status = parameters.Deserialize<DpsCaptureStatusDto>(JsonOptions);
                    if (status == null) break;
                    _captureRunning = status.Status is "starting" or "running";
                    CaptureStatusReceived?.Invoke(status);
                    break;
                }
                case "event.core.warning":
                case "event.core.error":
                {
                    var message = parameters.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString() ?? "未知 Core 诊断"
                        : "未知 Core 诊断";
                    var level = method.EndsWith("warning", StringComparison.Ordinal)
                        ? "warning"
                        : "error";
                    if (level == "warning")
                        _logService.LogWarning($"[DPS] Core 警告：{message}");
                    else
                        _logService.LogError($"[DPS] Core 错误：{message}");
                    DiagnosticReceived?.Invoke(level, message);
                    break;
                }
            }
        }

        private static DpsCoreException CreateRpcException(JsonElement error)
        {
            var code = error.TryGetProperty("code", out var codeElement)
                ? codeElement.GetInt32()
                : -32603;
            var message = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? "Core RPC 调用失败"
                : "Core RPC 调用失败";
            var domainCode = "RPC_ERROR";
            if (error.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("domain_code", out var domainElement))
            {
                domainCode = domainElement.GetString() ?? domainCode;
            }
            return new DpsCoreException(code, domainCode, message);
        }

        private static T? DeserializeResult<T>(JsonElement result)
        {
            if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
            return result.Deserialize<T>(JsonOptions);
        }

        private void FailPending(Exception error)
        {
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var completion))
                    completion.TrySetException(error);
            }
        }

        private async Task TerminateAndCleanupLockedAsync(
            Process process, CancellationToken cancellationToken)
        {
            try
            {
                try { process.StandardInput.Close(); }
                catch (InvalidOperationException) { }

                if (!process.HasExited)
                {
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    waitCts.CancelAfter(ProcessExitTimeoutMs);
                    try
                    {
                        await process.WaitForExitAsync(waitCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                            process.WaitForExit(ProcessExitTimeoutMs);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logService.LogDebug($"[DPS] Core 进程终止兜底：{ex.Message}");
            }
            finally
            {
                CleanupProcessLocked();
            }
        }

        private void CleanupProcessLocked()
        {
            var process = _process;
            _process = null;
            _stdoutTask = null;
            _stderrTask = null;
            _handshaken = false;
            _captureRunning = false;
            CoreVersion = "";

            if (_processCts != null)
            {
                _processCts.Cancel();
                _processCts.Dispose();
                _processCts = null;
            }
            process?.Dispose();

            if (_coreRunning)
            {
                _coreRunning = false;
                RaiseCoreStateChanged(false);
            }
            FailPending(new IOException("DPS Core 会话已关闭。"));
        }

        private void RaiseCoreStateChanged(bool isRunning)
        {
            try { CoreStateChanged?.Invoke(isRunning); }
            catch (Exception ex)
            {
                _logService.LogWarning($"[DPS] Core 状态事件订阅方异常：{ex.Message}");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DpsCoreService));
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                using var timeout = new CancellationTokenSource(DisposeTimeoutMs);
                ShutdownAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logService.LogDebug($"[DPS] Core Dispose 收尾：{ex.Message}");
            }
            finally
            {
                _disposed = true;
                _requestGate.Dispose();
            }
        }

        private sealed class HelloResult
        {
            [JsonPropertyName("core_version")]
            public string CoreVersion { get; set; } = "";

            [JsonPropertyName("protocol_version")]
            public int ProtocolVersion { get; set; }

            [JsonPropertyName("capabilities")]
            public string[] Capabilities { get; set; } = Array.Empty<string>();
        }

        private sealed class CaptureStartResult
        {
            [JsonPropertyName("operation_id")]
            public string OperationId { get; set; } = "";
        }
    }
}
