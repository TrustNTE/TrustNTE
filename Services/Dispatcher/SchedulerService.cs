using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Models;
using TrustNTE.Services.Interfaces;
using TrustNTE.Services.Scripting;

namespace TrustNTE.Services
{
    /// <summary>
    /// 配置组读写（同步原子写）。本文件含两个类：SchedulerGroupService（存储）+ SchedulerService（执行引擎）。
    ///    脚本目录已迁移到 UserData\Scripts（与 JsScriptViewModel / ScriptEngineService 三处同路径）。
    /// </summary>
    public static class SchedulerGroupService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string Dir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "Scheduler");

        /// <summary>   脚本目录（UserData\Scripts）。</summary>
        public static string ScriptsDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "Scripts");

        public static IReadOnlyList<SchedulerGroupDoc> List() =>
            Directory.Exists(Dir)
                ? Directory.GetFiles(Dir, "*.json")
                    .Select(LoadFromFile).Where(d => d != null).Cast<SchedulerGroupDoc>()
                    .OrderBy(d => d.Name).ToList()
                : new List<SchedulerGroupDoc>();

        private static SchedulerGroupDoc? LoadFromFile(string path)
        {
            try
            {
                var doc = JsonSerializer.Deserialize<SchedulerGroupDoc>(File.ReadAllText(path));
                if (doc is { Schema: 1 }) return doc;
                try { File.Copy(path, path + ".bak", true); } catch { }
                return null;
            }
            catch
            {
                try { File.Copy(path, path + ".bak", true); } catch { }
                return null;
            }
        }

        public static void Save(SchedulerGroupDoc doc)
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, Sanitize(doc.Name) + ".json");
            var tmp = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts));
            File.Move(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { }
        }

        public static void Delete(string name)
        {
            var p = Path.Combine(Dir, Sanitize(name) + ".json");
            if (File.Exists(p)) File.Delete(p);
        }

        private static string Sanitize(string name)
        {
            var clean = new string(name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray()).Trim();
            return string.IsNullOrEmpty(clean) ? "未命名分组" : clean;
        }
    }

    /// <summary>
    /// 调度执行引擎（公开 API）。组/任务双层取消（Stop 全停 / SkipCurrent 跳过当前）、
    /// 并发守卫、Shell 进程树清理、按任务超时、状态事件。
    /// </summary>
    public sealed class SchedulerService
    {
        private readonly ScriptEngineService _jsEngine;
        private readonly ILogService _log;
        private readonly SemaphoreSlim _runnerLock = new(1, 1);
        private readonly object _sync = new();
        private CancellationTokenSource? _groupCts;
        private CancellationTokenSource? _currentTaskCts;
        private Process? _currentProcess;
        private volatile bool _running;

        public bool IsRunning => _running;

        /// <summary>(组名, 任务名, 状态文本)。</summary>
        public event Action<string, string, string>? OnTaskStateChanged;
        /// <summary>执行状态变化（true 开始 / false 结束）。</summary>
        public event Action<bool>? OnRunningChanged;

        public SchedulerService(ScriptEngineService jsEngine, ILogService log)
        { _jsEngine = jsEngine; _log = log; }

        /// <summary>执行单个配置组（组内按顺序、按 RunNum 循环）。</summary>
        public async Task RunGroupAsync(string groupName, CancellationToken external)
        {
            if (!await _runnerLock.WaitAsync(0))
            {
                _log.LogWarning("[调度器] 已有分组正在执行中，请先停止当前任务");
                return;
            }
            _running = true;
            using var groupCts = CancellationTokenSource.CreateLinkedTokenSource(external);
            lock (_sync) _groupCts = groupCts;
            SafeInvoke(OnRunningChanged, true);
            try
            {
                var group = SchedulerGroupService.List().FirstOrDefault(g => g.Name == groupName)
                    ?? throw new InvalidOperationException($"配置组「{groupName}」不存在");
                _log.LogInfo($"[调度器] 开始执行分组: {group.Name}");
                await RunTasksAsync(group, groupCts.Token);
            }
            finally
            {
                lock (_sync) { _currentTaskCts = null; _groupCts = null; }
                _running = false;
                SafeInvoke(OnRunningChanged, false);
                _runnerLock.Release();
            }
        }

        /// <summary>连续执行多个配置组（组间 1s 缓冲）。</summary>
        public async Task RunGroupsAsync(IEnumerable<string> groupNames, CancellationToken external)
        {
            foreach (var name in groupNames.ToList())
            {
                if (external.IsCancellationRequested) break;
                await RunGroupAsync(name, external);
                try { await Task.Delay(1000, external); } catch (OperationCanceledException) { break; }
            }
        }

        private async Task RunTasksAsync(SchedulerGroupDoc group, CancellationToken groupToken)
        {
            foreach (var t in group.Tasks.ToList())
            {
                if (groupToken.IsCancellationRequested) break;
                if (!t.Enabled) { _log.LogInfo($"[{group.Name}] {t.Name} 已禁用，跳过"); continue; }

                for (int i = 0; i < Math.Max(1, t.RunNum); i++)
                {
                    if (groupToken.IsCancellationRequested) break;
                    using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(groupToken);
                    lock (_sync) _currentTaskCts = taskCts;
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        Notify(group.Name, t.Name, $"执行中 {i + 1}/{Math.Max(1, t.RunNum)}");
                        _log.LogInfo($"[{group.Name}] ▶ {t.Name}（第{i + 1}/{Math.Max(1, t.RunNum)}次）");
                        await RunTaskAsync(t, taskCts.Token);
                        Notify(group.Name, t.Name, $"完成 {sw.Elapsed.TotalSeconds:F0}s");
                        _log.LogInfo($"[{group.Name}] ✔ {t.Name} 完成，耗时 {sw.Elapsed.TotalSeconds:F1}s");
                    }
                    catch (OperationCanceledException) when (taskCts.IsCancellationRequested && !groupToken.IsCancellationRequested)
                    {
                        Notify(group.Name, t.Name, "已跳过");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        Notify(group.Name, t.Name, "已停止");
                        break;
                    }
                    catch (TimeoutException)
                    {
                        Notify(group.Name, t.Name, "执行超时");
                        _log.LogWarning($"[{group.Name}] {t.Name} 超时（{t.TimeoutSeconds}s）");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Notify(group.Name, t.Name, "执行失败");
                        _log.LogError($"[{group.Name}] ✘ {t.Name} 异常: {ex.Message}");
                        break;
                    }
                    finally { lock (_sync) _currentTaskCts = null; }
                }
            }
        }

        private async Task RunTaskAsync(SchedulerTaskItem t, CancellationToken ct)
        {
            switch (t.Type)
            {
                case "JsScript":
                    var path = Path.Combine(SchedulerGroupService.ScriptsDir, t.Target);
                    if (!File.Exists(path)) throw new FileNotFoundException($"脚本不存在: {t.Target}");
                    var code = await File.ReadAllTextAsync(path, ct);
                    var timeoutMs = t.TimeoutSeconds > 0 ? t.TimeoutSeconds * 1000 : 0;
                    await _jsEngine.ExecuteScriptAsync(code, timeoutMs, ct);
                    break;

                case "Shell":
                    await ExecuteShellAsync(t, ct);
                    break;

                case "KeyMouse":
                    _log.LogWarning($"[{t.Name}] KeyMouse 宏回放尚未接线，跳过");
                    break;

                default:
                    _log.LogWarning($"未知任务类型 {t.Type}，跳过");
                    break;
            }
        }

        private async Task ExecuteShellAsync(SchedulerTaskItem t, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {t.Target}",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogInfo($"[Shell 输出] {e.Data}"); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogError($"[Shell 错误] {e.Data}"); };
            if (!process.Start()) throw new InvalidOperationException("Shell 进程启动失败");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (_sync) _currentProcess = process;
            try { await process.WaitForExitAsync(ct); }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                lock (_sync) { if (_currentProcess == process) _currentProcess = null; }
            }
        }

        /// <summary>停止整个分组执行（当前任务一并取消）。</summary>
        public void Stop()
        {
            CancellationTokenSource? cts; lock (_sync) cts = _groupCts;
            try { cts?.Cancel(); } catch { }
            try { _jsEngine.StopScript(); } catch { }
            KillCurrentProcess();
        }

        /// <summary>只跳过当前任务，组内后续任务继续。</summary>
        public void SkipCurrent()
        {
            CancellationTokenSource? cts; lock (_sync) cts = _currentTaskCts;
            try { cts?.Cancel(); } catch { }
            try { _jsEngine.StopScript(); } catch { }
            KillCurrentProcess();
        }

        private void KillCurrentProcess()
        {
            Process? p; lock (_sync) p = _currentProcess;
            try { p?.Kill(entireProcessTree: true); } catch { }
        }

        private void Notify(string group, string task, string status)
        { try { OnTaskStateChanged?.Invoke(group, task, status); } catch (Exception ex) { _log.LogError($"[调度器] 事件广播异常: {ex.Message}"); } }

        private void SafeInvoke(Action<bool>? e, bool v)
        { try { e?.Invoke(v); } catch (Exception ex) { _log.LogError($"[调度器] 事件广播异常: {ex.Message}"); } }
    }
}
