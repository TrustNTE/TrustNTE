using Microsoft.ClearScript.V8;
using Microsoft.ClearScript;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Services;   
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Scripting
{
    public class ScriptEngineService : IDisposable
    {
        private V8ScriptEngine? _engine;
        private readonly ILogService _logService;
        private readonly JsGameApi _jsApi;
        private readonly JsVisionApi _vision;
        private readonly string _scriptDir;

        private readonly SemaphoreSlim _executeLock = new(1, 1);
        private readonly object _engineLock = new();

        private readonly object _tokenGate = new();
        private CancellationToken _currentToken = CancellationToken.None;

        private volatile bool _stopRequested;

        private CancellationToken ReadToken() { lock (_tokenGate) return _currentToken; }
        private void WriteToken(CancellationToken t) { lock (_tokenGate) _currentToken = t; }

        public event Action<string, string>? OnJsLog;

        private void RaiseJsLog(string level, string msg)
        { try { OnJsLog?.Invoke(level, msg); } catch { } }

        //构造参数加了 game/render/ocr —— DI 全部已注册，自动注入，App.xaml.cs 不用动
        public ScriptEngineService(ILogService logService, IInputRouter router,
            IAutomationContext autoCtx, IGameService game,
            NteRenderContextManager render, IOcrService ocr)
        {
            _logService = logService;
            _jsApi = new JsGameApi(router, autoCtx, logService);
            _jsApi.DriveProvider = () => router.CurrentDrive;
            _vision = new JsVisionApi(game, autoCtx, render, ocr); 
            _scriptDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "Scripts");
            Directory.CreateDirectory(_scriptDir);
        }

        private void InitializeEngine()
        {
            _engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);

            _engine.AddHostObject("log", new
            {
                info = new Action<string>(msg => { _logService.LogInfo($"[JS] {msg}"); RaiseJsLog("info", msg); }),
                error = new Action<string>(msg => { _logService.LogError($"[JS] {msg}"); RaiseJsLog("error", msg); }),
                debug = new Action<string>(msg => { _logService.LogDebug($"[JS] {msg}"); RaiseJsLog("debug", msg); })
            });

            _engine.AddHostObject("file", new LimitedFile(_scriptDir));
            _engine.AddHostObject("vision", _vision);                // JS 里直接用 vision

            _engine.AddHostObject("sleep", new Action<int>(ms =>
            {
                if (ms < 0) ms = 0;
                var token = ReadToken();
                long deadline = Environment.TickCount64 + ms;
                while (Environment.TickCount64 < deadline)
                {
                    if (token.IsCancellationRequested || _stopRequested) return;
                    var remain = deadline - Environment.TickCount64;
                    Thread.Sleep((int)Math.Min(50, Math.Min(int.MaxValue, remain)));
                }
            }));

            _engine.AddHostObject("automation", _jsApi);
        }

        private static string Wrap(string code) => "(async () => {\r\n" + code + "\r\n})()";

        public bool TryCompile(string code, out string error)
        {
            try
            {
                using var e = new V8ScriptEngine();
                e.Compile(Wrap(code));
                error = "";
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public async Task<string> ExecuteScriptAsync(string scriptContent, int timeoutMs, CancellationToken externalToken = default)
        {
            await _executeLock.WaitAsync(externalToken);
            Task<object?>? executeTask = null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            if (timeoutMs > 0) cts.CancelAfter(timeoutMs);

            _stopRequested = false;
            WriteToken(cts.Token);
            _vision.ClearCache();                                    // 模板缓存清空（改模板立即生效）
            _vision.StopRequested = () => _stopRequested || ReadToken().IsCancellationRequested;
            _jsApi.StopRequested = () => _stopRequested || ReadToken().IsCancellationRequested;

            try
            {
                lock (_engineLock) { InitializeEngine(); }

                var wrapped = Wrap(scriptContent);
                executeTask = Task.Run(() => (object?)_engine!.Evaluate(wrapped), cts.Token);

                var completedTask = await Task.WhenAny(executeTask, Task.Delay(Timeout.Infinite, cts.Token));

                if (completedTask != executeTask)
                {
                    lock (_engineLock) { _engine?.Interrupt(); }
                    if (externalToken.IsCancellationRequested)
                        throw new OperationCanceledException("脚本被外部中断", externalToken);
                    else
                        throw new TimeoutException("脚本执行超时。");
                }

                object? result = await executeTask;

                if (result is Task jsTask)
                {
                    var asyncCompletedTask = await Task.WhenAny(jsTask, Task.Delay(Timeout.Infinite, cts.Token));
                    if (asyncCompletedTask != jsTask)
                    {
                        lock (_engineLock) { _engine?.Interrupt(); }
                        if (externalToken.IsCancellationRequested)
                            throw new OperationCanceledException("脚本被外部中断", externalToken);
                        else
                            throw new TimeoutException("脚本异步等待超时。");
                    }
                    result = jsTask.GetType().GetProperty("Result")?.GetValue(jsTask);
                }

                return HandleJsResult(result);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw;
            }
            catch (ScriptInterruptedException)
            {
                throw new OperationCanceledException("脚本收到中断信号。");
            }
            catch (ScriptEngineException ex) when (ex.InnerException is OperationCanceledException
                                                    || ex.InnerException is ScriptInterruptedException
                                                    || cts.IsCancellationRequested)
            {
                throw new OperationCanceledException("脚本已停止");
            }
            catch (Exception ex)
            {
                _logService.LogError($"❌ 脚本运行时异常: {ex.Message}");
                throw;
            }
            finally
            {
                WriteToken(CancellationToken.None);
                _jsApi.ReleaseHeldKeys();   // 停止/超时/出错都批量释放按住的键,防WASD卡键

                if (executeTask != null && !executeTask.IsCompleted)
                {
                    lock (_engineLock) { _engine?.Interrupt(); }
                    try { await executeTask; } catch { }
                }

                lock (_engineLock)
                {
                    _engine?.Dispose();
                    _engine = null;
                }

                _stopRequested = false;
                _executeLock.Release();
            }

        }

        public void StopScript()
        {
            _stopRequested = true;
            lock (_engineLock) { _engine?.Interrupt(); }
        }

        private string HandleJsResult(object? jsResult)
        {
            if (jsResult == null) return string.Empty;
            if (jsResult is string || jsResult is int || jsResult is bool || jsResult is double)
                return jsResult.ToString()!;
            try
            {
                var json = _engine?.Script.JSON.stringify(jsResult);
                return json?.ToString() ?? "{}";
            }
            catch { return jsResult.ToString() ?? string.Empty; }
        }

        public void Dispose()
        {
            lock (_engineLock) { _engine?.Dispose(); _engine = null; }
            _executeLock.Dispose();
        }
    }
}
