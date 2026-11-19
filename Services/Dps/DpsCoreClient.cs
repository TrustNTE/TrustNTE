using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Dps
{
    /// <summary>
    /// nte-core Sidecar 客户端实现。
    /// 协议依据:nte-dps-toolkit/docs/CLI_PROTOCOL_ZH.md(serve --stdio,JSON-RPC 2.0 over NDJSON)。
    /// 请求逐字手写(协议字段为 snake_case 原文,避免映射层);响应按 id 匹配,无 id 按 method 路由事件。
    /// </summary>
    public sealed class DpsCoreClient : IDpsCoreClient
    {
        private const string ExeArgs = "serve --stdio";                 // 协议原文:启动命令行
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly ILogService _log;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        private Process? _proc;
        private CancellationTokenSource? _cts;
        private int _reqId;
        private volatile bool _capturing;

        public event Action<string>? CaptureStatusChanged;
        public event Action<JsonElement>? BattleSummaryReceived;
        public event Action<string, bool>? CoreLog;

        public bool IsRunning => _capturing;

        public DpsCoreClient(ILogService log) => _log = log;

        // ══════════ 生命周期 ══════════
        public async Task StartAsync(string coreExePath, string? deviceName, CancellationToken token)
        {
            if (_proc is not null && !_proc.HasExited) throw new InvalidOperationException("nte-core 已在运行");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _pending.Clear();

            var psi = new ProcessStartInfo(coreExePath)
            {
                Arguments = ExeArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(coreExePath) ?? AppContext.BaseDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("nte-core 进程启动失败");
            _proc.EnableRaisingEvents = true;
            _proc.Exited += OnProcExited;

            _ = ReadStdoutLoop(_proc, _cts.Token);      // 协议要求:发送请求前先启动常驻 stdout reader
            _ = DrainStderrLoop(_proc, _cts.Token);

            await RequestAsync("core.hello",
    JsonSerializer.Serialize(new
    {
        client_name = "TrustNTE",
        client_version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "0.0.0",
        protocol_min = 1,
        protocol_max = 1
    }));

            _log.LogInfo("[DPS] nte-core 握手完成(协议版本 1)");

            var detect = await RequestAsync("capture.detect", "{}");
            _log.LogInfo($"[DPS] capture.detect:游戏进程={GetBool(detect, "game_process_detected")}, " +
                         $"推荐网卡={(GetString(detect, "recommended_device") ?? "无(自动)")}, 网卡数={(detect.TryGetProperty("devices", out var dv) ? dv.GetArrayLength() : 0)}");

            // device:mode:auto(原项目默认,按 HTGame 活动连接自动选)或 mode:name(指定 pcap 设备名)
            var deviceJson = string.IsNullOrWhiteSpace(deviceName)
                ? "{\"mode\":\"auto\"}"
                : $"{{\"mode\":\"name\",\"name\":\"{Escape(deviceName)}\"}}";
            await RequestAsync("capture.start",
                $"{{\"profile\":\"combat\",\"device\":{deviceJson},\"include_incoming\":true,\"server_damage_calibration\":true,\"raw_capture\":\"disabled\"}}");
            // raw_capture:"disabled" = 不创建 PCAP 文件(对齐风险页「原始 PCAP 落盘默认关闭」)
            _log.LogInfo("[DPS] combat 抓包已启动(raw 落盘关闭 · 时停口径=扣除时停)");
        }

        public async Task StopAsync()
        {
            try
            {
                if (_proc is { HasExited: false })
                {
                    if (_capturing)
                        await RequestAsync("capture.stop", "{}");   // 等最终可靠摘要与 stopped 事件
                    await RequestAsync("core.shutdown", "{}");
                }
            }
            catch (Exception ex) { _log.LogWarning($"[DPS] 核心停止异常:{ex.Message}"); }
            finally
            {
                _cts?.Cancel();
                KillProc();
                _capturing = false;
                CaptureStatusChanged?.Invoke("stopped");
            }
        }

        public async Task ResetBattleAsync()
        {
            if (!_capturing) return;
            await RequestAsync("battle.reset", "{}");
            _log.LogInfo("[DPS] battle.reset:核心战斗统计已重置");
        }

        // ══════════ stdout 读取与路由 ══════════
        private async Task ReadStdoutLoop(Process proc, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested &&
                       await proc.StandardOutput.ReadLineAsync(token) is { } line)
                {
                    if (line.Length == 0) continue;                 // 协议:空行忽略
                    HandleLine(line);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogWarning($"[DPS] stdout 读取中断:{ex.Message}"); }
        }

        private void HandleLine(string line)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { _log.LogWarning($"[DPS] 非 JSON 输出:{Truncate(line)}"); return; }

            using var _ = doc;
            var root = doc.RootElement;

            // 响应:含 id,按 id 匹配(协议:响应与事件交错,不得假定下一行就是响应)
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null)
            {
                var key = idEl.ToString();
                if (_pending.TryRemove(key, out var tcs)) tcs.TrySetResult(root.Clone());
                return;
            }

            // 事件:无 id,按 method 路由(仅消费 DPS 页需要的;inventory.snapshot 本页不接)
            if (!root.TryGetProperty("method", out var mEl)) return;
            switch (mEl.GetString())
            {
                case "event.capture.status":
                    var st = root.TryGetProperty("params", out var p1) &&
                             p1.TryGetProperty("status", out var s1) ? s1.GetString() : "unknown";
                    if (st == "running") _capturing = true;
                    else if (st == "stopped") _capturing = false;
                    CaptureStatusChanged?.Invoke(st ?? "unknown");
                    break;
                case "event.battle.summary":
                    if (root.TryGetProperty("params", out var p2))
                        BattleSummaryReceived?.Invoke(p2.Clone());  // Clone:doc 释放后仍可用
                    break;
                case "event.core.warning":
                    CoreLog?.Invoke(root.TryGetProperty("params", out var p3) ? p3.GetRawText() : "", false);
                    break;
                case "event.core.error":
                    CoreLog?.Invoke(root.TryGetProperty("params", out var p4) ? p4.GetRawText() : "", true);
                    break;
            }
        }

        private async Task DrainStderrLoop(Process proc, CancellationToken token)
        {   // 协议:stderr 专用于英文诊断日志
            try
            {
                while (!token.IsCancellationRequested &&
                       await proc.StandardError.ReadLineAsync(token) is { } line)
                    _log.LogDebug($"[DPS][core] {line}");
            }
            catch (OperationCanceledException) { }
            catch { /* 进程退出时流关闭,忽略 */ }
        }

        // ══════════ 请求 ══════════
        private async Task<JsonElement> RequestAsync(string method, string paramsJson)
        {
            var id = $"req-{Interlocked.Increment(ref _reqId)}";
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            await _proc!.StandardInput.WriteLineAsync(
                $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"{method}\",\"params\":{paramsJson}}}");
            await _proc.StandardInput.FlushAsync();

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout));
            if (winner != tcs.Task)
            { _pending.TryRemove(id, out _); throw new TimeoutException($"{method} 响应超时"); }

            var resp = tcs.Task.Result;
            if (resp.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("domain_code", out var dc) ? dc.GetString() : err.GetRawText();
                throw new InvalidOperationException($"{method} 失败:{code}");   // 领域错误码原文,如 NPCAP_NOT_FOUND
            }
            return resp.TryGetProperty("result", out var r) ? r : default;
        }

        private void OnProcExited(object? s, EventArgs e)
        {
            _capturing = false;
            _logService_ProcessExited();
            CaptureStatusChanged?.Invoke("stopped");
        }
        private void _logService_ProcessExited() => _log.LogWarning("[DPS] nte-core 进程已退出");

        private void KillProc()
        {
            try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
        }

        public void Dispose() { _cts?.Cancel(); KillProc(); }

        // ── 小工具 ──
        private static string? GetString(JsonElement e, string n)
            => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static bool GetBool(JsonElement e, string n)
            => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string Truncate(string s) => s.Length > 120 ? s[..120] + "…" : s;
    }
}
