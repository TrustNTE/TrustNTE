using OpenCvSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TrustNTE.Helpers;
using TrustNTE.Services.Triggers;
using TrustNTE.Views.Windows;
using TrustNTE.Models;

namespace TrustNTE.Services
{
    public partial class AutomationService
    {


        // 主循环进程降频检查字段
        private DateTime _lastProcCheckUtc = DateTime.MinValue;
        private bool _gameProcessAlive = true;

        private async Task WorkflowLoopAsync(CancellationToken token)
        {
            var config = _configService.Load();
            IntPtr hwnd = config.StartGameTogether ? await _supervisor.RunStartupFlowAsync(config, token) : await _gameService.WaitForGameWindowAsync(token);

            if (token.IsCancellationRequested) return;
            if (hwnd == IntPtr.Zero)
            {
                if (token.IsCancellationRequested) return;
                _logService.LogError("❌ 没有找到异环游戏窗口！");
                if (!App.IsAutoStartCommand)
                {
                    Application.Current?.Dispatcher.Invoke(() => new StartErrorWindow { Owner = Application.Current.MainWindow }.ShowDialog());
                }
                return;
            }

            CurrentHwnd = hwnd;
            _engineStartUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _frameCount, 0);
            _lastHeartbeatUtc = _engineStartUtc;
            _heartbeatBaseFrames = 0;
            _everBoundGame = true;
            _logService.Log("成功绑定异环窗口句柄");
            if (Win32Input.GetClientRect(hwnd, out var initialRc))
            {
                _lastTrackedW = initialRc.Right - initialRc.Left;
                _lastTrackedH = initialRc.Bottom - initialRc.Top;
                _lastTrackedDpi = Win32Input.GetDpiForWindow(hwnd) / 96.0;
                _logService.LogInfo($"[NteCore] 已绑定渲染窗口 0x{hwnd.ToInt64():X} | 视口尺寸: {_lastTrackedW}x{_lastTrackedH} | 缩放DPI: {_lastTrackedDpi:F2}");
            }

            LogClickModeIfChanged(config.MouseDriveType);
            Application.Current?.Dispatcher.Invoke(() => OnWindowBound?.Invoke());

            int bootInterval = int.TryParse(config.TriggerInterval, out int bootMs) ? Math.Clamp(bootMs, 10, 2000) : 50;
            _gameService.StartCapture(hwnd, config.CaptureMode, bootInterval);
            _triggerManager.InitializeAll(BuildTriggerContext(hwnd));

            while (!token.IsCancellationRequested)
            {
                var cfgNow = _configService.Load();
                LogClickModeIfChanged(cfgNow.MouseDriveType);

                //    进程存活检查降频：每帧枚举全进程表太贵（5~30ms），3 秒一次足够
                if ((DateTime.UtcNow - _lastProcCheckUtc).TotalSeconds >= 3)
                {
                    _lastProcCheckUtc = DateTime.UtcNow;
                    _gameProcessAlive = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameConstants.GameExe)).Any();
                }
                if (!_gameProcessAlive)
                {
                    if (!cfgNow.AutoRestartOnCrash) { _logService.LogWarning("⚠️ 检测到游戏已退出，自动停止截图引擎。"); break; }
                    _gameService.StopCapture();
                    var newHwnd = await _supervisor.RestartAfterCrashAsync(cfgNow, token);
                    if (newHwnd is IntPtr nh)
                    {
                        CurrentHwnd = nh;
                        hwnd = nh;

                        if (Win32Input.GetClientRect(nh, out var rrc))
                        {
                            _lastTrackedW = rrc.Right - rrc.Left;
                            _lastTrackedH = rrc.Bottom - rrc.Top;
                            _lastTrackedDpi = Win32Input.GetDpiForWindow(nh) / 96.0;
                            _logService.LogInfo($"[NteCore] 崩溃重启，重新绑定 0x{nh.ToInt64():X} | 视口 {_lastTrackedW}x{_lastTrackedH} | DPI {_lastTrackedDpi:F2}");
                        }

                        int restartInterval = int.TryParse(cfgNow.TriggerInterval, out int rMs) ? Math.Clamp(rMs, 10, 2000) : 50;
                        _gameService.StartCapture(nh, cfgNow.CaptureMode, restartInterval);

                        _triggerManager.DisposeAll();
                        _triggerManager.InitializeAll(BuildTriggerContext(nh));
                        continue;
                    }
                    break;
                }

                
                int currentInterval = int.TryParse(cfgNow.TriggerInterval, out int parsedMs) ? Math.Clamp(parsedMs, 10, 2000) : 50;

                _gameService.UpdateCaptureInterval(currentInterval);
                var utcNow = DateTime.UtcNow;
                if ((utcNow - _lastHeartbeatUtc).TotalSeconds >= 60)
                {
                    long frames = Interlocked.Read(ref _frameCount);
                    double fps = (frames - _heartbeatBaseFrames) / (utcNow - _lastHeartbeatUtc).TotalSeconds;
                    _logService.LogInfo($"[引擎] 心跳 | 区间 {fps:F1} FPS | 累计 {frames:N0} 帧");
                    _lastHeartbeatUtc = utcNow;
                    _heartbeatBaseFrames = frames;
                }

                try
                {
                    using var mat = _gameService.CaptureWindow();
                    if (mat != null && !mat.Empty())
                    {
                        Interlocked.Increment(ref _frameCount);
                        long frameAge = _gameService.GetCaptureFrameAgeMs();
                        if (frameAge > Math.Max(currentInterval * 5, 500))
                        {
                            if ((DateTime.Now - _lastStaleWarnTime).TotalSeconds > 5)
                            {
                                _lastStaleWarnTime = DateTime.Now;
                                _logService.LogWarning($"[截图] 画面已 {frameAge}ms 未更新，截图通道疑似卡死，执行单发兜底");
                            }
                            _gameService.ForceSingleShot();

                        }

                        Mat processMat = mat.Clone();
                        try
                        {
                            if (Win32Input.GetClientRect(CurrentHwnd, out var crc) && crc.Right > crc.Left)
                            {
                                int w = crc.Right - crc.Left;
                                int h = crc.Bottom - crc.Top;
                                double dpi = Win32Input.GetDpiForWindow(CurrentHwnd) / 96.0;

                                bool changed = w != _lastTrackedW || h != _lastTrackedH || Math.Abs(dpi - _lastTrackedDpi) > 0.01;
                                if (changed)
                                {
                                    if (_lastTrackedW > 0 && (DateTime.Now - _lastResizeLogAt).TotalMilliseconds > 1000)
                                    {
                                        _logService.LogInfo($"[NteCore] 检测到窗口变化: 视口 {_lastTrackedW}x{_lastTrackedH} → {w}x{h} | DPI {_lastTrackedDpi:F2} → {dpi:F2} | 归一化系数已实时同步");
                                        _lastResizeLogAt = DateTime.Now;

                                        double aspect = (double)w / h;
                                        if (Math.Abs(aspect - 16.0 / 9.0) > 0.05)
                                        {
                                            _logService.LogWarning($"[NteCore] 当前视口 {w}x{h} 宽高比偏离 16:9，模板匹配可能失准，建议恢复 16:9 分辨率！");
                                        }
                                    }
                                    _lastTrackedW = w;
                                    _lastTrackedH = h;
                                    _lastTrackedDpi = dpi;
                                }
                                _nteRenderCtx.UpdateNteContext(w, h);
                            }
                            else
                            {
                                _nteRenderCtx.UpdateNteContext(processMat.Width, processMat.Height);
                            }

                            Mat syncedMat = _nteRenderCtx.SyncNteRenderFrame(processMat);
                            processMat.Dispose();
                            processMat = syncedMat;

                            await _supervisor.InspectGameFrameAsync(processMat, hwnd, cfgNow, token);

                            lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = processMat.Clone(); }

                            if (IsPreviewWindowOpen && OnFrameCaptured != null && Interlocked.Exchange(ref _isUiRunning, 1) == 0)
                            {
                                var uiMat = processMat.Clone();
                                var dispatchToken = _dispatchCts!.Token;
                                _ = Task.Run(() =>
                                {
                                    try { dispatchToken.ThrowIfCancellationRequested(); _triggerManager.DrawDebug(uiMat); OnFrameCaptured?.Invoke(ImageHelper.MatToBitmapSource(uiMat)); }
                                    catch (OperationCanceledException) { }
                                    finally { uiMat.Dispose(); Interlocked.Exchange(ref _isUiRunning, 0); }
                                });
                            }

                            if (cfgNow.IsCombatDebugWindowOpen && OnCombatDebugFrameCaptured != null && Interlocked.Exchange(ref _isCombatDebugRunning, 1) == 0)
                            {
                                var debugMat = processMat.Clone();
                                var dispatchToken = _dispatchCts!.Token;
                                _ = Task.Run(() =>
                                {
                                    try { dispatchToken.ThrowIfCancellationRequested(); _triggerManager.DrawCombatDebug(debugMat); OnCombatDebugFrameCaptured?.Invoke(ImageHelper.MatToBitmapSource(debugMat)); }
                                    catch (OperationCanceledException) { }
                                    finally { debugMat.Dispose(); Interlocked.Exchange(ref _isCombatDebugRunning, 0); }
                                });
                            }


                            if (Interlocked.Exchange(ref _isBizRunning, 1) == 0)
                            {
                                var bizMat = processMat.Clone();
                                var dispatchToken = _dispatchCts!.Token;
                                _ = Task.Run(() =>
                                {
                                    try { dispatchToken.ThrowIfCancellationRequested(); _triggerManager.Dispatch(bizMat, 1.0, dispatchToken); }
                                    catch (OperationCanceledException) { }
                                    catch (Exception ex) { _logService.LogError($"业务识别异常: {ex.Message}"); }
                                    finally { bizMat.Dispose(); Interlocked.Exchange(ref _isBizRunning, 0); }
                                });
                            }
                        }
                        finally { processMat.Dispose(); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logService.LogError($"主循环异常: {ex.Message}"); }
                await Task.Delay(currentInterval, token);
            }
        }
    }
}