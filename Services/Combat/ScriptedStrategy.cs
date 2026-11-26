using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Helpers;
using TrustNTE.Models;
using TrustNTE.Services.Interfaces;
using TrustNTE.Services.Scripting;
using TrustNTE.Services.Triggers;

namespace TrustNTE.Services.Combat
{
    /// <summary>
    /// JS 脚本战斗策略：StrategyDocument.Team 顺序轮转，每个角色一段 JS 连招。
    /// 持久 V8 引擎——JS 全局状态跨轮转保留（可做连招计数/冷却记忆）。
    /// 脱战判定复刻 DefaultReactiveStrategy（含 Q 宽限），脱战时打断连招。
    /// JS 契约（宿主对象 ctx）：PressQ/Press/Attack/LockTarget/SwitchNext/SwitchTo/Sleep
    ///   + HasEnemy/EnemyX~EnemyH/EnemySource/EnemyConf/QReady/QInCd/QBright
    /// </summary>
    public sealed class ScriptedStrategy : ICombatStrategy, IDisposable
    {
        public string Name => $"JS·{_doc.Info.Name}";

        private const double QGraceSeconds = 3.0;          // 与 DefaultReactiveStrategy 对齐
        private const double ScriptTimeoutSeconds = 15.0;  // 单角色连招上限，防死循环卡轮转
        private const int MinScriptMs = 800;               // 最短演出时间，防空槽脚本刷屏轮转

        private readonly StrategyDocument _doc;
        private readonly ILogService _log;
        private readonly JsGameApi _api;
        private readonly string[] _scripts;
        private readonly V8ScriptEngine _engine;

        private CancellationTokenSource? _cts;
        private Task? _scriptTask;
        private DateTime _scriptStartedAt;
        private int _charIndex;
        private bool _engaged;
        private DateTime _lastSeenTime = DateTime.Now;

        public ScriptedStrategy(StrategyDocument doc, IInputRouter router,
                                IAutomationContext auto, ILogService log)
        {
            _doc = doc;
            _log = log;
            _api = new JsGameApi(router, auto, log);

            // 真实 schema：Team 是 List<string?>（角色名），默认含 4 个 null 槽 → 过滤空位
            var team = doc.Team.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (team.Count == 0)
                throw new ArgumentException("策略队伍为空（引擎守卫已拦截，此为双保险）");

            // 同一角色可有多段动作（Index 字段）：按 Index 升序拼接为一段连招。
            // Index==0 视为未设置，排末尾且稳定排序保持 JSON 原序（全默认时不乱序）。
            _scripts = team.Select(ch => string.Join("\n",
                    doc.Actions.Where(a => a.Character == ch)
                               .OrderBy(a => a.Index == 0 ? int.MaxValue : a.Index)
                               .Select(a => a.Action)
                               .Where(s => !string.IsNullOrWhiteSpace(s))))
                .ToArray();

            _engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
            _engine.AddHostObject("ctx", _api);
            _engine.AddHostObject("log", new
            {
                info = new Action<string>(m => log.LogInfo($"[策略·{doc.Info.Name}] {m}")),
                error = new Action<string>(m => log.LogError($"[策略·{doc.Info.Name}] {m}"))
            });
            _log.LogInfo($"[策略] 「{doc.Info.Name}」上线 | 队伍 {string.Join("/", team)} | 脚本 {_scripts.Length} 段");
        }

        public void Reset()
        {
            StopScript();
            _scriptTask = null;
            _engaged = false;
            _charIndex = 0;
            _lastSeenTime = DateTime.Now;
        }

        public void Decide(CombatFrameContext ctx)
        {
            // 驱动：与 CombatStrategyEngine.DriveFor 逐字对齐（战斗后台模式 → 强制后台）
            _api.DriveProvider = () => ctx.Config.CombatBackgroundMode
                ? InputDriveType.Background : _api.RouterDrive;

            // 刷新 JS 感知快照
            _api.UpdateSnapshot(ctx.HasEnemy, ctx.EnemyRect, ctx.EnemySource, ctx.EnemyConf,
                                ctx.QReady, ctx.QInCastCooldown, ctx.QBright);

            // 目击刷新 + Q动画宽限
            if (ctx.HasEnemy) _lastSeenTime = ctx.Now;
            else if ((ctx.Now - _api.LastQCastUtc).TotalSeconds < QGraceSeconds)
                _lastSeenTime = ctx.Now;

            // 脱战：接敌态下真盲满阈值 → 打断连招收摊
            if (_engaged && (ctx.Now - _lastSeenTime).TotalSeconds >= ctx.Config.LostCombatSeconds)
            {
                StopScript();
                _engaged = false;
                _log.LogInfo($"[策略·{_doc.Info.Name}] 目标丢失超过{ctx.Config.LostCombatSeconds:0.#}秒，脱战收招。");
                return;
            }

            if (!_engaged)
            {
                if (!ctx.HasEnemy) return;   // 搜索中静默
                _engaged = true;
                _charIndex = 0;
                _scriptTask = null;
                _log.LogInfo($"[策略·{_doc.Info.Name}] 接敌开招（{ctx.EnemySource} conf={ctx.EnemyConf:F2}）");
            }

            // 连招进行中：超时保护，否则让路
            if (_scriptTask is { IsCompleted: false })
            {
                if ((ctx.Now - _scriptStartedAt).TotalSeconds > ScriptTimeoutSeconds)
                {
                    _log.LogWarning($"[策略] 角色脚本超{ScriptTimeoutSeconds:0}s未完，中断轮转");
                    StopScript();
                }
                else return;
            }

            // 上一段完成 → 轮转下一位
            if (_scriptTask != null)
            {
                if ((ctx.Now - _scriptStartedAt).TotalMilliseconds < MinScriptMs) return;
                _charIndex = (_charIndex + 1) % _scripts.Length;
                _log.LogDebug($"[策略] 轮转 → 第{_charIndex + 1}位 {_doc.Team.ElementAtOrDefault(_charIndex)}");
            }

            StartScript();
        }

        private void StartScript()
        {
            var old = _scriptTask;
            if (old != null) { try { old.Wait(300); } catch { } }   // 引擎单线程访问，等旧任务退场

            _cts = new CancellationTokenSource();
            var cts = _cts;
            _scriptStartedAt = DateTime.Now;
            var idx = _charIndex;
            var body = string.IsNullOrWhiteSpace(_scripts[idx])
                ? "ctx.Attack(); await ctx.Sleep(400);"   // 空槽 = 平A过渡，自然轮转
                : _scripts[idx];

            _scriptTask = Task.Run(() =>
            {
                try
                {
                    var r = _engine.Evaluate($"(async()=>{{\n{body}\n}})()");
                    if (r is Task t) t.Wait();
                }
                //    子类必须在基类之前：ClearScript 的 ScriptInterruptedException
                //   继承自 OperationCanceledException，顺序反了报 CS0160
                catch (ScriptInterruptedException) { }
                catch (OperationCanceledException) { }
                // Task.Wait 把内部异常包进 AggregateException，拆包识别中断类
                catch (AggregateException ae) when (ae.InnerException is OperationCanceledException
                                                    or ScriptInterruptedException)
                { }
                catch (Exception ex)
                {
                    _log.LogError($"[策略·{_doc.Info.Name}] 第{idx + 1}位脚本异常: {ex.Message}");
                }
            }, cts.Token);
        }

        private void StopScript()
        {
            try { _cts?.Cancel(); } catch { }
            try { _engine.Interrupt(); } catch { }
        }

        public void Dispose()
        {
            StopScript();
            _engine.Dispose();
            _cts?.Dispose();
        }
    }
}
