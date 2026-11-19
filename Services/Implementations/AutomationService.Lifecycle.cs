using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using TrustNTE.Helpers;

namespace TrustNTE.Services
{
    public partial class AutomationService
    {
        public void Start()
        {
            _lastUserEngineActionUtc = DateTime.UtcNow;

            if (IsEngineRunning && CurrentHwnd != IntPtr.Zero)
            {
                _logService.LogInfo("[引擎] 已在运行，忽略重复启动请求。");
                return;
            }

            if (!_workflowLock.Wait(0))
            {
                _logService.LogWarning("[引擎] 正在启动或停止中，已忽略本次启动请求（稍后可再试）。");
                return;
            }

            _captureEverStarted = true;
            EnsureIdleGuardStarted();
            _cts = new CancellationTokenSource();
            _dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var token = _cts.Token;

            OnRunningStateChanged?.Invoke(true);

            _workflowTask = Task.Run(async () =>
            {
                try
                {
                    await WorkflowLoopAsync(token);
                }
                catch (OperationCanceledException)
                {
                    _logService.Log("自动化任务已手动停止。");
                }
                catch (Exception ex)
                {
                    _logService.LogError($"自动化任务发生错误: {ex.Message}");
                }
                finally
                {
                    if (_engineStartUtc != DateTime.MinValue)
                    {
                        var run = DateTime.UtcNow - _engineStartUtc;
                        string dur = $"{(int)run.TotalHours}:{run.Minutes:D2}:{run.Seconds:D2}";
                        _logService.LogInfo($"[引擎] 本次运行 {dur} | 处理 {Interlocked.Read(ref _frameCount):N0} 帧 | 已安全收摊。");
                        _engineStartUtc = DateTime.MinValue;
                    }

                    _gameService.StopCapture();
                    _dispatchCts?.Cancel();
                    await WaitForBizIdleAsync(TimeSpan.FromSeconds(1.5));
                    try { _router.ReleaseAllHeldKeys(CurrentHwnd); } catch { }
                    _workflowLock.Release();
                    CurrentHwnd = IntPtr.Zero;
                    OnRunningStateChanged?.Invoke(false);
                    _triggerManager.DisposeAll();
                    lock (_frameLock)
                    {
                        _latestFrame?.Dispose();
                        _latestFrame = null;
                    }
                    _dispatchCts?.Dispose();
                    _dispatchCts = null;
                }
            });
        }

        public void Stop()
        {
            _lastUserEngineActionUtc = DateTime.UtcNow;
            _logService.Log("正在停止自动化引擎...");
            _cts?.Cancel();
            _dispatchCts?.Cancel();
        }

        public void ShutdownForExit()
        {
            try { _idleGuardCts.Cancel(); } catch { }
            try { _cts?.Cancel(); } catch { }
            try { _dispatchCts?.Cancel(); } catch { }
        }

        public void WaitForExit(int timeoutMs)
        {
            var t = _workflowTask;
            if (t == null) return;
            try { t.Wait(timeoutMs); } catch { }
        }

        private async Task WaitForBizIdleAsync(TimeSpan budget)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < budget)
            {
                if (Volatile.Read(ref _isBizRunning) == 0 && Volatile.Read(ref _isUiRunning) == 0 && Volatile.Read(ref _isCombatDebugRunning) == 0) return;
                await Task.Delay(20);
            }
        }

        private void EnsureIdleGuardStarted()
        {
            if (Interlocked.Exchange(ref _idleGuardStarted, 1) == 1) return;
            _ = Task.Run(() => IdleGuardLoopAsync(_idleGuardCts.Token));
        }

        private async Task IdleGuardLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token);
                    if (!_captureEverStarted) continue;
                    if (!_configService.Load().IdleGuardEnabled) continue;

                    if (IsEngineRunning || TaskRuntimeRegistry.Count > 0) continue;
                    if ((DateTime.UtcNow - _lastUserEngineActionUtc).TotalSeconds < 30) continue;

                    var cfg = _configService.Load();

                    if (!Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameConstants.GameExe)).Any())
                    {
                        if (!_everBoundGame) continue;
                        if (cfg.AutoRestartOnCrash) await _supervisor.RestartAfterCrashAsync(cfg, token, fromIdleGuard: true);
                        continue;
                    }

                    if (_triggerManager.ExtraBusyCheck?.Invoke() ?? false) continue;

                    var hwnd = CurrentHwnd != IntPtr.Zero ? CurrentHwnd : FindGameWindowQuiet();
                    if (hwnd == IntPtr.Zero) continue;

                    Mat? frame;
                    if (!_captureGate.Wait(0)) continue;
                    try { frame = _gameService.CaptureWindowOf(hwnd); }
                    finally { _captureGate.Release(); }

                    if (frame == null || frame.Empty()) continue;

                    if (Win32Input.GetClientRect(hwnd, out var crc) && crc.Right > crc.Left)
                        _nteRenderCtx.UpdateNteContext(crc.Right - crc.Left, crc.Bottom - crc.Top);
                    var synced = _nteRenderCtx.SyncNteRenderFrame(frame);
                    frame.Dispose();
                    frame = synced;

                    try { await _supervisor.InspectIdleFrameAsync(frame, hwnd, cfg, token); }
                    finally { frame.Dispose(); }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private IntPtr FindGameWindowQuiet()
        {
            var p = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameConstants.GameExe)).FirstOrDefault();
            return p == null ? IntPtr.Zero : _gameService.FindWindowForProcess(p.Id, GameConstants.GameWindowClass);
        }
    }
}