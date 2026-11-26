using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Helpers;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Triggers
{
    /// <summary>
    /// 每日活跃度自动领取触发器（画面驱动版 v10.3 - 按UID(账号)独立记录 + OCR识别UID）。
    /// 流程：判断是否需要按F1（★先OCR识别UID→按账号判定是否需要领取）→ 点击每日活跃界面
    ///   → 判断今日活跃度 → 点击领取奖励 → 再次判断是否后续还要进入
    ///   → 不需要就停止识别F1、到重查时间再识别。
    /// ★ UID识别（OCR直接调用 OnnxOcrService，经 TriggerContext.OcrRecognize 委托注入）：
    ///   ① 主界面确认后、按F1前，OCR左下角UID区域 Rect(42,1055,136,25)，取最长≥6位数字串为UID
    ///   ② OCR异步执行：先克隆ROI再丢后台线程，序号防串写、超时6s作废，不阻塞帧线程
    ///   ③ 探测节奏：流程开始前必识别一次；静默期每60秒探测一次（抓运行中切账号）
    ///   ④ UID连续3次识别失败 → 回退旧全局字段 LastDailyActiveClaimDate
    ///   ⑤ 落盘写 LastClaimByUid[uid] + 同步旧字段；每日重置时清理30天前旧条目
    /// v10.3 变更：
    ///   ★ 调试快照[活跃度·调试]走 LogDebug 级别：日志窗口点「全部」或「DEBUG」可见，
    ///     点「INFO」自动隐藏（不再刷屏）；独立文件 Logs/DailyActive_debug.log 继续保留
    ///   ★ OCR周期探测日志去重：UID和判定结果都不变时静默
    ///   ★ 待命态蓝礼盒需连续2帧确认（防加载界面颜色误报导致空点）
    ///   ★ 修编译警告 CS0162(const→static readonly) / CS8629(hit.Value前加null防御)
    /// v9 状态机保留：FlowStep(主页待命→已按F1→已点入口→活跃度页→撤退中)，
    ///   盲点入口仅限未确认进入活跃度页；所有ESC带[识别依据]；撤退前检查当帧证据。
    /// 档位判定：蓝色礼盒=可领取→点礼盒中心；粉色✔=已领取→永远不点。
    /// </summary>
    public class DailyActiveTrigger : ITaskTrigger
    {
        public string Name => "每日活跃度领取";
        public TriggerClass Class => TriggerClass.Reactive;
        public bool IsEnabled { get; set; } = true;

        // ===== ★ 流程步骤状态机 =====
        private enum FlowStep
        {
            Idle,           // 主页待命（未在流程中，等重查时间）
            WaitGuide,      // 已按F1，等指南页打开
            EntryClicked,   // 已点入口，等活跃度页证据确认
            InActivity,     // 已确认进入活跃度页
            Retreating,     // 撤退中（持续ESC直到确认回主界面）
        }
        private FlowStep _step = FlowStep.Idle;

        private static string StepName(FlowStep s) => s switch
        {
            FlowStep.Idle => "主页待命",
            FlowStep.WaitGuide => "已按F1",
            FlowStep.EntryClicked => "已点入口",
            FlowStep.InActivity => "活跃度页",
            FlowStep.Retreating => "撤退中",
            _ => "?",
        };

        // ===== 调试输出（v10.3：主日志走LogDebug级别 + 独立文件双通道）=====
        // ★ static readonly 而非 const：避免 CS0162（编译器常量折叠报无法访问代码）
        private static readonly bool DebugFileEnabled = true;   // 独立文件通道（重启后可翻历史）
        private const int DebugHeartbeatSec = 10;   // 稳态每10秒一条心跳
        private const int DebugMinGapMs = 800;  // 变化触发的最小间隔

        // ★ 独立调试文件（主日志INFO保持干净，需要翻历史时打开这个文件）
        private static readonly string DebugFilePath =
            Path.Combine(AppContext.BaseDirectory, "Logs", "DailyActive_debug.log");
        private static readonly object DebugFileLock = new();
        private const long DebugFileMaxBytes = 2 * 1024 * 1024;   // 超过2MB自动清空重写
        private const int DebugRingCapacity = 300;               // 内存环形缓冲条数
        private readonly Queue<string> _debugRing = new();
        private bool _debugSessionHeaderWritten;

        // ===== 图集模板组映射 =====
        private const string GroupF1Button = "Nte_F1DailyRewardStartButton";
        private const string GroupActivity = "Nte_ActivityLevel";
        private const string GroupClaim = "Nte_ActivityLevelClaim";
        private const string GroupGetItem = "Nte_GetItem";
        private const string GroupUpperLimit = "Nte_DailyActivityReachUpperLimit";

        // ===== ROI（1920x1080 实测校准）=====
        private static readonly Rect F1DetectRoi = new(1348, 13, 38, 32); // 主界面 F1 按钮
        private static readonly Rect EntryRoi = new(85, 338, 62, 58); // 指南页活跃度入口（中心116,367）
        private static readonly Rect BadgeRoi = new(95, 322, 55, 40); // 入口红点角标区
        private static readonly Rect ClaimRoi1 = new(428, 812, 58, 31); // 领取按钮 1
        private static readonly Rect ClaimRoi2 = new(743, 814, 61, 28); // 领取按钮 2
        private static readonly Rect GetItemRoi = new(906, 448, 107, 42); // 获得物品弹窗
        private static readonly Rect UpperLimitRoi = new(345, 808, 224, 36); // 今日已达上限横幅

        // ★ UID OCR 区（用户实测标定：x2=178, y2=1080，宽高136x25）
        private static readonly Rect UidOcrRoi = new(42, 1055, 136, 25);

        // 5个档位（礼盒/粉✔同一排）
        private static readonly Rect[] TierRois =
        {
            new( 646, 244, 48, 52),   // 20档
            new( 901, 244, 48, 52),   // 40档
            new(1141, 244, 48, 52),   // 60档
            new(1381, 244, 48, 52),   // 80档
            new(1624, 244, 48, 52),   // 100档
        };

        private static readonly Rect[] ProgressRois =
        {
            new( 493, 265, 145, 10), new( 723, 265, 163, 10), new( 962, 265, 163, 10),
            new(1201, 265, 165, 10), new(1440, 265, 165, 10),
        };

        // ===== 颜色阈值（HSV）=====
        private const byte BlueHMin = 100, BlueHMax = 125;  // 蓝礼盒=可领取
        private const byte PinkHMin = 150, PinkHMax = 178;  // 粉✔=已领取
        private const byte GreenHMin = 78, GreenHMax = 92;  // 进度条
        private const byte ColorSMin = 120, ColorVMin = 150;
        private const double BlueRatioMin = 0.10;
        private const double PinkRatioMin = 0.15;
        private const double GreenRatioMin = 0.30;
        // 红点：红0-22（红+橙红）+ 品红158-180
        private const byte BadgeH1Min = 0, BadgeH1Max = 22;
        private const byte BadgeH2Min = 158, BadgeH2Max = 180;
        private const byte BadgeSMin = 90, BadgeVMin = 110;
        private const double BadgeRatioMin = 0.02;

        // ===== 节奏 / 看门狗 =====
        private const int ScanIntervalMs = 500;
        private const int DupActionMs = 1500;
        private const int IdleEscSec = 4;
        private const int IdleEscActivitySec = 2;
        private const int CleanupWindowSec = 12;
        private const int FlowMaxSec = 120;
        private const int RecheckIntervalMin = 3;
        private const int ReturnEscIntervalSec = 3;
        private const int ReturnEscMax = 6;
        private const int ReturnEscSlowSec = 20;
        private const int GuideWaitMs = 1200;
        private const int EntryRetryMs = 1600;
        private const int EntryClickMax = 3;
        private const int BlindEntryDelayMs = 1500;
        private const int StartupGraceSec = 4;
        private const int BlindF1ConfirmMs = 700;
        private const int BlindF1Max = 2;
        private const int BlindF1RetryMs = 2500;
        private const int RetreatAmbiguousGiveUpSec = 10;
        private const int RetreatingFuseSec = 40;

        // ===== ★ 待命态蓝礼盒确认 =====
        private const int IdleBlueConfirmFrames = 2;   // 待命态蓝礼盒需连续2帧确认（防加载界面颜色误报）

        // ===== ★ UID OCR 节奏 =====
        private const int UidOcrTimeoutMs = 6000;   // 单次OCR超时
        private const int UidOcrMaxAttempts = 3;      // 连续失败上限（之后退避）
        private const int UidRetryGapMs = 1500;   // 两次OCR尝试最小间隔
        private const int UidProbeSec = 60;     // 静默期周期探测间隔（抓切账号）
        private const int UidBackoffSec = 30;     // 连续失败后的退避
        private const int UidMinDigits = 6;      // UID最少位数
        private const int UidRecordKeepDays = 30;     // 配置里UID记录保留天数

        private readonly IConfigService _configService;
        private readonly IInputRouter _router;
        private readonly ILogService _logService;
        private readonly IntPtr _hwnd;
        private readonly NteDailyActiveTemplateService _templates;
        private readonly Func<Mat, Task<string?>>? _ocrRecognize;   // ★ OCR委托（TriggerContext注入）

        private DateTime _lastScanUtc = DateTime.MinValue;
        private DateTime _firstFrameAt = DateTime.MinValue;
        private string _attemptsGameDay = string.Empty;
        private string _giveUpGameDay = string.Empty;
        private string _doneGameDay = string.Empty;
        private string _doneUid = string.Empty;
        private string _stateDumpDay = string.Empty;
        private string _lastClickTarget = string.Empty;
        private DateTime _lastClickTime = DateTime.MinValue;
        private DateTime _flowStartTime = DateTime.MinValue;
        private DateTime _lastActionTime = DateTime.MinValue;
        private int _lastProgressMask = -1;
        private DateTime _recheckUntil = DateTime.MinValue;
        private int _entryClickCount;
        private DateTime _lastEntryClickTime = DateTime.MinValue;
        private DateTime _guideWaitUntil = DateTime.MinValue;
        private DateTime _blindF1FirstSeen = DateTime.MinValue;
        private int _blindF1Count;
        private DateTime _retreatStart = DateTime.MinValue;
        private int _returnEscCount;
        private DateTime _nextReturnEscTime = DateTime.MinValue;
        private bool _bannerCapNoted;

        // ===== ★ UID 状态 =====
        private string _currentUid = string.Empty;
        private bool _uidKnown;
        private bool _uidWantOcr = true;
        private volatile bool _uidOcrBusy;
        private volatile string? _pendingUidRaw;
        private int _uidOcrSeq;
        private DateTime _uidOcrStartedAt;
        private DateTime _lastUidAttemptAt = DateTime.MinValue;
        private DateTime _uidBackoffUntil = DateTime.MinValue;
        private DateTime _nextUidProbeAt = DateTime.MinValue;
        private int _uidFailCount;
        private bool _uidRoiWarned;
        private long _lastUidOcrMs;
        private bool _lastUidVerdict;                 // 上次打印时的判定结果（日志去重用）

        // ===== ★ 待命态蓝礼盒确认计数 =====
        private int _idleBlueStreak;

        // ===== 每帧识别快照 =====
        private bool? _dbgF1Raw, _dbgF1Valid, _dbgBanner, _dbgPopup, _dbgEntryTpl, _dbgEntryBadge, _dbgClaim;
        private bool _dbgBadgeRaw;
        private int _dbgPinkCount = -1, _dbgBlueVisible = -1, _dbgBlueFirst = -1, _dbgProgressVisible = -1;
        private string _dbgAction = "无";
        private string _lastDbgSig = string.Empty;
        private DateTime _lastDbgLogUtc = DateTime.MinValue;

        public DailyActiveTrigger(
            IConfigService configService, IInputRouter router,
            ILogService logService, IntPtr hwnd,
            Func<Mat, Task<string?>>? ocrRecognize = null)
        {
            _configService = configService;
            _router = router;
            _logService = logService;
            _hwnd = hwnd;
            _templates = new NteDailyActiveTemplateService(logService);
            _ocrRecognize = ocrRecognize;
            if (_ocrRecognize == null)
                _logService.LogWarning("[OCR] 未注入OCR委托 → UID识别不可用，回退全局记录判定（旧版行为）");
        }

        public bool IsFlowActive => _step != FlowStep.Idle;

        private bool IsDuplicateClick(string target)
            => target == _lastClickTarget && (DateTime.Now - _lastClickTime).TotalMilliseconds < DupActionMs;

        private void MarkClick(string target)
        {
            _lastClickTarget = target;
            _lastClickTime = DateTime.Now;
        }

        private void ResetFlowState()
        {
            _step = FlowStep.Idle;
            _entryClickCount = 0;
            _guideWaitUntil = DateTime.MinValue;
            _retreatStart = DateTime.MinValue;
            _returnEscCount = 0;
            _blindF1Count = 0;
            _blindF1FirstSeen = DateTime.MinValue;
            _lastProgressMask = -1;
            _idleBlueStreak = 0;
            // 注意：不动 _uidWantOcr / _currentUid —— 静默期每帧都会走这里，不能反复触发OCR
        }

        /// <summary>★ 按UID落盘；旧全局字段同步写（向后兼容/界面显示）</summary>
        private bool MarkDone(Models.AppConfig config, string gameDay)
        {
            string uidTag = _uidKnown ? _currentUid : "unknown";
            if (_doneGameDay == gameDay && _doneUid == uidTag) return false;
            bool changed = false;
            if (_uidKnown)
            {
                if (!config.LastClaimByUid.TryGetValue(_currentUid, out var d) || d != gameDay)
                {
                    config.LastClaimByUid[_currentUid] = gameDay;
                    changed = true;
                }
            }
            if (config.LastDailyActiveClaimDate != gameDay)
            {
                config.LastDailyActiveClaimDate = gameDay;
                changed = true;
            }
            if (changed) _configService.Save(config);
            _doneGameDay = gameDay;
            _doneUid = uidTag;
            _logService.LogInfo($"[记录] 今日活跃度奖励已全部领取 UID={uidTag} 今日将不再打开F1（{gameDay}）");
            return true;
        }

        /// <summary>★ 按UID判定今日是否已领完；UID未知时回退旧全局字段</summary>
        private bool IsDoneForAccount(Models.AppConfig config, string gameDay)
        {
            if (_uidKnown)
                return config.LastClaimByUid.TryGetValue(_currentUid, out var d) && d == gameDay;
            return config.LastDailyActiveClaimDate == gameDay;
        }

        private static string GetGameDay(int resetHour)
        {
            var t = DateTime.Now;
            int h = Math.Clamp(resetHour, 0, 23);
            return (t.TimeOfDay >= new TimeSpan(h, 0, 0)
                ? t.Date : t.Date.AddDays(-1)).ToString("yyyy-MM-dd");
        }

        /// <summary>★ 清理超过保留期的UID记录（每日重置时调用一次）</summary>
        private void PurgeOldUidEntries(Models.AppConfig config, string gameDay)
        {
            if (config.LastClaimByUid.Count == 0) return;
            if (!DateTime.TryParse(gameDay, out var today)) return;
            var stale = new List<string>();
            foreach (var kv in config.LastClaimByUid)
            {
                if (!DateTime.TryParse(kv.Value, out var d)) { stale.Add(kv.Key); continue; }
                if ((today - d).TotalDays > UidRecordKeepDays) stale.Add(kv.Key);
            }
            if (stale.Count == 0) return;
            foreach (var k in stale) config.LastClaimByUid.Remove(k);
            _configService.Save(config);
            _logService.LogInfo($"[记录] 清理 {stale.Count} 条超过{UidRecordKeepDays}天的UID领取记录");
        }

        // ───────── ★ 调试输出：独立文件 + 环形缓冲 ─────────

        /// <summary>调试快照写入独立文件（不进主日志INFO）；IO失败绝不影响触发器运行</summary>
        private void AppendDebug(string line)
        {
            if (!DebugFileEnabled) return;
            string entry = $"{DateTime.Now:HH:mm:ss.fff} {line}";
            lock (_debugRing)
            {
                _debugRing.Enqueue(entry);
                while (_debugRing.Count > DebugRingCapacity) _debugRing.Dequeue();
            }
            try
            {
                lock (DebugFileLock)
                {
                    var dir = Path.GetDirectoryName(DebugFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var fi = new FileInfo(DebugFilePath);
                    if (fi.Exists && fi.Length > DebugFileMaxBytes) fi.Delete();   // 超限清空重写
                    if (!_debugSessionHeaderWritten)
                    {
                        _debugSessionHeaderWritten = true;
                        File.AppendAllText(DebugFilePath,
                            $"\r\n===== 会话开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n");
                    }
                    File.AppendAllText(DebugFilePath, entry + Environment.NewLine);
                }
            }
            catch { /* 文件IO失败静默忽略 */ }
        }

        /// <summary>
        /// 手动导出：把内存环形缓冲（最近300条调试快照）一次性写入主日志。
        /// 需要在界面上加"导出活跃度调试"按钮时调用本方法即可。
        /// </summary>
        public void DumpDebugToLog()
        {
            lock (_debugRing)
            {
                _logService.LogInfo($"[活跃度·调试导出] 共{_debugRing.Count}条（内存保留最近{DebugRingCapacity}条）");
                foreach (var l in _debugRing) _logService.LogInfo("[活跃度·调试] " + l);
            }
        }

        // ───────── ★ UID OCR（直接调用 OnnxOcrService）─────────

        private static string ExtractLongestDigits(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string best = string.Empty;
            foreach (Match m in Regex.Matches(raw, @"\d+"))
                if (m.Value.Length > best.Length) best = m.Value;
            return best;
        }

        private static string FirstLine(string msg) => (msg ?? string.Empty).Split('\n')[0];

        /// <summary>发起一次UID OCR：立即克隆ROI（帧缓冲会被复用），后台线程识别，序号防串写</summary>
        private void BeginUidOcr(Mat frame, DateTime now)
        {
            if (_ocrRecognize == null) { _uidWantOcr = false; return; }
            if (UidOcrRoi.Bottom > frame.Height || UidOcrRoi.Right > frame.Width)
            {
                if (!_uidRoiWarned)
                {
                    _uidRoiWarned = true;
                    _logService.LogError($"[OCR] UID ROI {UidOcrRoi} 超出帧尺寸 {frame.Width}x{frame.Height}，UID识别不可用");
                }
                _uidWantOcr = false;
                return;
            }
            _uidOcrBusy = true;
            _uidOcrStartedAt = now;
            _lastUidAttemptAt = now;
            _uidWantOcr = false;
            int seq = Interlocked.Increment(ref _uidOcrSeq);
            var snap = new Mat(frame, UidOcrRoi).Clone();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _ = Task.Run(async () =>
            {
                string? text = null;
                try { text = await _ocrRecognize(snap).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logService.LogError($"[OCR] UID识别异常: {FirstLine(ex.Message)}");
                    text = null;
                }
                finally { try { snap.Dispose(); } catch { } }
                sw.Stop();
                if (seq == Volatile.Read(ref _uidOcrSeq))
                {
                    _pendingUidRaw = text;
                    _lastUidOcrMs = sw.ElapsedMilliseconds;
                    _uidOcrBusy = false;
                }
            });
        }

        /// <summary>★ UID状态机：超时作废 / 周期探测 / 发起OCR / 消费结果。每帧调用。</summary>
        private void UpdateUid(Mat frame, DateTime now, Models.AppConfig config, string gameDay,
            bool mainLike, bool inGrace)
        {
            // 超时作废在途任务
            if (_uidOcrBusy && (now - _uidOcrStartedAt).TotalMilliseconds > UidOcrTimeoutMs)
            {
                Interlocked.Increment(ref _uidOcrSeq);
                _uidOcrBusy = false;
                OnUidAttemptFailed(now, "超时");
            }

            // 发起条件：需要识别 + 主界面可信 + 非宽限期 + 非流程中 + 退避/间隔已过
            bool probeDue = _step == FlowStep.Idle && mainLike && now >= _nextUidProbeAt;
            if (!_uidOcrBusy && !_uidRoiWarned && _ocrRecognize != null && !inGrace
                && _step == FlowStep.Idle && mainLike
                && (_uidWantOcr || probeDue)
                && now >= _uidBackoffUntil
                && (now - _lastUidAttemptAt).TotalMilliseconds >= UidRetryGapMs)
            {
                if (probeDue && !_uidWantOcr) _uidFailCount = 0;
                _uidWantOcr = false;
                BeginUidOcr(frame, now);
            }

            // 消费结果
            if (!_uidOcrBusy && _pendingUidRaw != null)
            {
                string raw = _pendingUidRaw;
                _pendingUidRaw = null;
                string digits = ExtractLongestDigits(raw);
                if (digits.Length >= UidMinDigits)
                {
                    // ★ 去重：只有UID变化或判定翻转才写主日志；60秒周期探测同结果 → 静默
                    bool uidChanged = _currentUid != digits;
                    if (uidChanged)
                    {
                        _logService.LogInfo($"[OCR] UID识别成功 原文\"{raw}\" → UID={digits} (耗时{_lastUidOcrMs}ms)");
                        if (_currentUid.Length > 0)
                            _logService.LogWarning($"[活跃度] UID变化: {_currentUid} → {digits}（疑似切换账号，重算领取状态）");
                        _currentUid = digits;
                        _uidKnown = true;
                        _doneGameDay = string.Empty;   // UID变了 → 强制重算done
                        _doneUid = string.Empty;
                    }
                    _uidFailCount = 0;
                    _nextUidProbeAt = now.AddSeconds(UidProbeSec);
                    bool doneNow = IsDoneForAccount(config, gameDay);
                    if (uidChanged || doneNow != _lastUidVerdict)
                    {
                        _logService.LogInfo(
                            $"[活跃度] 账号UID={digits} | 该账号上次领完日=\"{(config.LastClaimByUid.TryGetValue(digits, out var d) ? d : string.Empty)}\" | " +
                            $"判定：{(doneNow ? "今日已领完-静默等待" : "今日未领完-到重查时间将自动领取")}");
                        _lastUidVerdict = doneNow;
                    }
                }
                else
                {
                    OnUidAttemptFailed(now, $"原文\"{raw}\"（未提取到≥{UidMinDigits}位数字）");
                }
            }
        }

        private void OnUidAttemptFailed(DateTime now, string why)
        {
            _uidFailCount++;
            if (_uidFailCount >= UidOcrMaxAttempts)
            {
                _uidFailCount = 0;
                _uidBackoffUntil = now.AddSeconds(UidBackoffSec);
                _logService.LogWarning($"[OCR] UID连续{UidOcrMaxAttempts}次识别失败（{why}）→ {UidBackoffSec}秒后重试；期间按{(_uidKnown ? "缓存UID" : "全局记录")}判定");
            }
            else
            {
                _logService.LogWarning($"[OCR] UID识别失败第{_uidFailCount}次（{why}）");
            }
            _nextUidProbeAt = now.AddSeconds(UidProbeSec);
        }

        // ───────── 识别依据构建 ─────────

        private static string BuildEvidence(bool entryTpl, bool claimBtn, int blue, int pink, int progress, bool popup, bool banner)
        {
            var p = new List<string>();
            if (popup) p.Add("获得物品弹窗");
            if (banner) p.Add("上限横幅");
            if (entryTpl) p.Add("活跃度入口模板");
            if (claimBtn) p.Add("领取按钮");
            if (blue > 0) p.Add($"蓝礼盒x{blue}");
            if (pink > 0) p.Add($"粉✔x{pink}");
            if (progress > 0) p.Add($"进度条{progress}/5");
            return p.Count > 0 ? string.Join("+", p) : "无";
        }

        private static string BuildFullSnapshot(bool f1Raw, bool entryTpl, bool badgeSeen, bool claimBtn,
            int blueCount, int pinkCount, int progressCount, bool popup, bool banner)
            => $"F1模板{(f1Raw ? "✔" : "✘")} 入口模板{(entryTpl ? "✔" : "✘")} 红点{(badgeSeen ? "✔" : "✘")} " +
               $"领取按钮{(claimBtn ? "✔" : "✘")} 蓝礼盒{blueCount} 粉✔{pinkCount} " +
               $"进度{progressCount}/5 弹窗{(popup ? "✔" : "✘")} 横幅{(banner ? "✔" : "✘")}";

        // ───────── 调试快照 ─────────

        private void ResetDbg()
        {
            _dbgF1Raw = _dbgF1Valid = _dbgBanner = _dbgPopup = _dbgEntryTpl = _dbgEntryBadge = _dbgClaim = null;
            _dbgBadgeRaw = false;
            _dbgPinkCount = _dbgBlueVisible = _dbgBlueFirst = _dbgProgressVisible = -1;
            _dbgAction = "无";
        }

        private static string Mark(bool? v) => v == null ? "?" : (v.Value ? "✔" : "✘");
        private static string Cnt(int v) => v < 0 ? "?" : v.ToString();
        private static string Idx(int v) => v < 0 ? "-" : (v + 1).ToString();

        private static int PopCount(int v) { int n = 0; while (v != 0) { v &= v - 1; n++; } return n; }

        private void EmitDebugLog()
        {
            string f1 = Mark(_dbgF1Raw);
            if (_dbgF1Raw == true && _dbgF1Valid == false) f1 = "压";
            string badge = Mark(_dbgEntryBadge);
            if (_dbgEntryBadge == false && _dbgBadgeRaw) badge = "屏";
            string uid = _uidOcrBusy ? "识中"
                : _uidKnown ? (_currentUid.Length > 4 ? "…" + _currentUid[^4..] : _currentUid)
                : "-";
            string sig =
                $"F1{f1} 横幅{Mark(_dbgBanner)} 弹窗{Mark(_dbgPopup)} " +
                $"入口{Mark(_dbgEntryTpl)} 红点{badge} 领取{Mark(_dbgClaim)} " +
                $"粉{Cnt(_dbgPinkCount)}/5 蓝{Cnt(_dbgBlueVisible)}/5首{Idx(_dbgBlueFirst)} " +
                $"进度{Cnt(_dbgProgressVisible)}/5 UID{uid} | 步骤:{StepName(_step)} | 动作:{_dbgAction}";
            var now = DateTime.UtcNow;
            bool changed = sig != _lastDbgSig;
            if (changed && (now - _lastDbgLogUtc).TotalMilliseconds < DebugMinGapMs) return;
            if (!changed && (now - _lastDbgLogUtc).TotalSeconds < DebugHeartbeatSec) return;
            _lastDbgSig = sig;
            _lastDbgLogUtc = now;
            // ★ v10.3 双通道：
            //   ① 独立文件（重启后可翻历史，2MB自动清理）
            //   ② LogDebug级别 → 日志窗口「全部」或「DEBUG」chip可见；「INFO」chip自动隐藏（不再刷屏）
            //      受开发者模式总闸控制：关闭开发者模式时 LogDebug 不进管线，主日志彻底干净
            AppendDebug($"[活跃度·调试] {sig}");
            _logService.LogDebug($"[活跃度·调试] {sig}");
        }

        // ───────── 颜色检测 ─────────

        private static bool IsColorPresent(Mat frame, Rect roi, byte hMin, byte hMax, double ratioMin)
        {
            if (roi.Bottom > frame.Height || roi.Right > frame.Width) return false;
            using var roiMat = new Mat(frame, roi);
            using var hsv = new Mat();
            using var mask = new Mat();
            Cv2.CvtColor(roiMat, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.InRange(hsv, new Scalar(hMin, ColorSMin, ColorVMin), new Scalar(hMax, 255, 255), mask);
            return Cv2.CountNonZero(mask) / (double)(roi.Width * roi.Height) >= ratioMin;
        }

        private static bool IsRedBadgePresent(Mat frame)
        {
            if (BadgeRoi.Bottom > frame.Height || BadgeRoi.Right > frame.Width) return false;
            using var roiMat = new Mat(frame, BadgeRoi);
            using var hsv = new Mat();
            using var mask = new Mat();
            using var mask1 = new Mat();
            using var mask2 = new Mat();
            Cv2.CvtColor(roiMat, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.InRange(hsv, new Scalar(BadgeH1Min, BadgeSMin, BadgeVMin), new Scalar(BadgeH1Max, 255, 255), mask1);
            Cv2.InRange(hsv, new Scalar(BadgeH2Min, BadgeSMin, BadgeVMin), new Scalar(BadgeH2Max, 255, 255), mask2);
            Cv2.BitwiseOr(mask1, mask2, mask);
            return Cv2.CountNonZero(mask) / (double)(BadgeRoi.Width * BadgeRoi.Height) >= BadgeRatioMin;
        }

        // ───────── 主入口 ─────────

        public void DrawDebug(Mat drawMat)
        {
            var config = _configService.Load();
            if (!IsEnabled || !config.DailyRewardEnabled) return;

            Cv2.Rectangle(drawMat, F1DetectRoi, new Scalar(0, 255, 255), 1);
            Cv2.Rectangle(drawMat, EntryRoi, new Scalar(255, 0, 255), 1);
            Cv2.Rectangle(drawMat, BadgeRoi, new Scalar(0, 165, 255), 1);
            Cv2.Rectangle(drawMat, ClaimRoi1, new Scalar(0, 255, 0), 1);
            Cv2.Rectangle(drawMat, ClaimRoi2, new Scalar(0, 255, 0), 1);
            Cv2.Rectangle(drawMat, GetItemRoi, new Scalar(0, 0, 255), 1);
            Cv2.Rectangle(drawMat, UpperLimitRoi, new Scalar(0, 0, 255), 1);
            Cv2.Rectangle(drawMat, UidOcrRoi, new Scalar(203, 192, 255), 1);
            foreach (var r in TierRois) Cv2.Rectangle(drawMat, r, new Scalar(0, 255, 0), 1);
            foreach (var r in ProgressRois) Cv2.Rectangle(drawMat, r, new Scalar(255, 255, 0), 1);

            if (_step != FlowStep.Idle)
                Cv2.PutText(drawMat, $"DailyActive:{StepName(_step)}",
                    new Point(F1DetectRoi.X - 160, Math.Max(12, F1DetectRoi.Y - 5)),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 0), 1);
        }

        public void OnCapture(Mat resizedMat, double scale)
        {
            if (_firstFrameAt == DateTime.MinValue) _firstFrameAt = DateTime.Now;

            var config = _configService.Load();
            if (!config.DailyRewardEnabled) { _step = FlowStep.Idle; return; }

            string gameDay = GetGameDay(config.DailyResetHour);
            if (_attemptsGameDay != gameDay)
            {
                _attemptsGameDay = gameDay;
                _giveUpGameDay = string.Empty;
                _doneGameDay = string.Empty;
                _doneUid = string.Empty;
                ResetFlowState();
                _recheckUntil = DateTime.MinValue;
                _nextReturnEscTime = DateTime.MinValue;
                _bannerCapNoted = false;
                _uidWantOcr = true;
                _uidFailCount = 0;
                PurgeOldUidEntries(config, gameDay);
            }
            if (_giveUpGameDay == gameDay) return;

            if ((DateTime.UtcNow - _lastScanUtc).TotalMilliseconds < ScanIntervalMs) return;
            _lastScanUtc = DateTime.UtcNow;

            ResetDbg();
            try { RunFrame(resizedMat, config, gameDay); }
            finally { EmitDebugLog(); }
        }

        // ───────── 单帧：全量识别 + 状态机决策 ─────────

        private void RunFrame(Mat frame, Models.AppConfig config, string gameDay)
        {
            var now = DateTime.Now;
            bool inGrace = (now - _firstFrameAt).TotalSeconds < StartupGraceSec;
            bool flowWindow = _step != FlowStep.Idle || (now - _lastActionTime).TotalSeconds < CleanupWindowSec;

            // ★ 每游戏日第一条日志：状态Dump（含UID）
            if (_stateDumpDay != gameDay)
            {
                _stateDumpDay = gameDay;
                _logService.LogInfo(
                    $"[活跃度] 当日状态 → 当前UID={(_uidKnown ? _currentUid : "未知(待OCR)")} | " +
                    $"该账号记录=\"{(_uidKnown && config.LastClaimByUid.TryGetValue(_currentUid, out var ud) ? ud : string.Empty)}\" | " +
                    $"全局旧字段=\"{config.LastDailyActiveClaimDate}\" | " +
                    $"当前游戏日={gameDay}(活跃度重置点={config.DailyResetHour}点)");
            }

            // ── ① 全量识别 ──
            bool popup = _templates.MatchAny(frame, GetItemRoi, GroupGetItem) != null;
            _dbgPopup = popup;

            bool banner = _templates.MatchAny(frame, UpperLimitRoi, GroupUpperLimit) != null;
            _dbgBanner = banner;

            bool f1Raw = _templates.MatchAny(frame, F1DetectRoi, GroupF1Button) != null
                      || _templates.MatchAny(frame, F1DetectRoi, GroupActivity) != null;

            bool entryTpl = _templates.MatchAny(frame, EntryRoi, GroupActivity) != null
                         || _templates.MatchAny(frame, EntryRoi, GroupF1Button) != null;
            _dbgEntryTpl = entryTpl;

            bool badgeRaw = IsRedBadgePresent(frame);
            _dbgBadgeRaw = badgeRaw;
            bool badgeSeen = !inGrace && !f1Raw && badgeRaw
                          && _step != FlowStep.InActivity && _step != FlowStep.Retreating;
            _dbgEntryBadge = badgeSeen;
            bool entryBadge = !entryTpl && badgeSeen;

            bool claimBtn = _templates.MatchAny(frame, ClaimRoi1, GroupClaim) != null
                         || _templates.MatchAny(frame, ClaimRoi2, GroupClaim) != null;
            _dbgClaim = claimBtn;

            int blueCount = 0, firstBlue = -1, pinkCount = 0;
            for (int i = 0; i < TierRois.Length; i++)
            {
                if (IsColorPresent(frame, TierRois[i], BlueHMin, BlueHMax, BlueRatioMin))
                {
                    blueCount++;
                    if (firstBlue < 0) firstBlue = i;
                }
                if (IsColorPresent(frame, TierRois[i], PinkHMin, PinkHMax, PinkRatioMin))
                    pinkCount++;
            }
            _dbgBlueVisible = blueCount;
            _dbgBlueFirst = firstBlue;
            _dbgPinkCount = pinkCount;

            int pMask = 0;
            for (int i = 0; i < ProgressRois.Length; i++)
                if (IsColorPresent(frame, ProgressRois[i], GreenHMin, GreenHMax, GreenRatioMin))
                    pMask |= 1 << i;
            int progressCount = PopCount(pMask);
            _dbgProgressVisible = progressCount;

            // 状态机推进：已按F1/已点入口 → 见到活跃度页铁证 → 确认进入活跃度页
            if ((_step == FlowStep.WaitGuide || _step == FlowStep.EntryClicked)
                && (claimBtn || blueCount > 0 || pinkCount > 0 || progressCount > 0))
            {
                _step = FlowStep.InActivity;
                _guideWaitUntil = DateTime.MinValue;
                _logService.LogInfo($"[活跃度] 已确认进入活跃度页 [识别依据: {BuildEvidence(entryTpl, claimBtn, blueCount, pinkCount, progressCount, popup, banner)}]");
            }

            // F1压制：只信强信号；刚操作2秒内不信；活跃度页流程中F1模板命中视为误匹配
            bool pageMarker = entryTpl || claimBtn || blueCount > 0 || pinkCount > 0 || progressCount > 0 || popup || banner;
            bool f1Valid = f1Raw && !pageMarker;
            if (f1Valid && _step != FlowStep.Idle && (now - _lastActionTime).TotalSeconds < 2.0) f1Valid = false;
            if (f1Valid && _step == FlowStep.InActivity) f1Valid = false;
            _dbgF1Raw = f1Raw;
            _dbgF1Valid = f1Valid;

            // 盲按F1资格（主界面但模板失灵）
            bool blindF1Eligible = _step == FlowStep.Idle && !inGrace && !f1Raw && !pageMarker && now >= _recheckUntil;

            // ── ② ★ UID状态机（在done计算前推进，保证本帧判定用最新UID）──
            UpdateUid(frame, now, config, gameDay, f1Valid || blindF1Eligible, inGrace);

            // ── ③ 按UID计算done ──
            bool done = IsDoneForAccount(config, gameDay);

            // ── ④ 决策 ──

            // ① 获得物品弹窗 → ESC
            if (flowWindow && popup)
            {
                if (!IsDuplicateClick("ESC弹窗"))
                {
                    _router.KeyPress(_hwnd, 0x1B);
                    _logService.LogInfo("[键鼠] 已获取环石按ESC返回 [识别依据: 获得物品弹窗模板]");
                    MarkClick("ESC弹窗");
                    _lastActionTime = now;
                    _dbgAction = "ESC关弹窗(依据:获得物品弹窗)";
                }
                else _dbgAction = "弹窗ESC防重复";
                return;
            }

            // ② 上限横幅：有可领→继续领；无可领→记录+撤退
            if (banner)
            {
                if (blueCount > 0)
                {
                    if (!_bannerCapNoted)
                    {
                        _logService.LogInfo("[提示] 活跃度已达上限但仍有可领档位 → 继续领取，不记录完成");
                        _bannerCapNoted = true;
                    }
                    _dbgAction = $"上限提示但有可领(蓝{blueCount})-继续领取";
                }
                else
                {
                    MarkDone(config, gameDay);
                    if (!IsDuplicateClick("ESC上限"))
                    {
                        _router.KeyPress(_hwnd, 0x1B);
                        _logService.LogInfo($"[键鼠] 按Esc返回主界面 [识别依据: 上限横幅模板 | 蓝礼盒{blueCount} 粉✔{pinkCount}]");
                        MarkClick("ESC上限");
                    }
                    _step = FlowStep.Retreating;
                    _retreatStart = now;
                    _returnEscCount = 0;
                    _nextReturnEscTime = now;
                    _lastActionTime = now;
                    _dbgAction = "ESC关上限横幅→撤退中";
                    return;
                }
            }
            else _bannerCapNoted = false;

            // ③ 已达上限（按UID记录）
            if (done)
            {
                if (blueCount > 0 || claimBtn)
                {
                    if (_uidKnown) config.LastClaimByUid.Remove(_currentUid);
                    config.LastDailyActiveClaimDate = string.Empty;
                    _configService.Save(config);
                    _doneGameDay = string.Empty;
                    _doneUid = string.Empty;
                    _logService.LogWarning($"[活跃度] 记录称已领完，但画面仍有可领元素（蓝{blueCount} 领取按钮{(claimBtn ? "✔" : "✘")}）→ 清除错误记录，继续领取");
                    done = false;
                }
                else
                {
                    if (f1Valid)
                    {
                        if (_returnEscCount > 0)
                            _logService.LogInfo($"[活跃度] 已确认回到主界面，撤退结束 UID={(_uidKnown ? _currentUid : "unknown")}");
                        _returnEscCount = 0;
                        ResetFlowState();
                        _uidWantOcr = true;
                        string rec = _uidKnown && config.LastClaimByUid.TryGetValue(_currentUid, out var rd) ? rd : "无记录";
                        _dbgAction = $"UID{(_uidKnown ? "…" + _currentUid[^4..] : "?")} 今日已领完(该账号记录:{rec})-主界面静默";
                        return;
                    }
                    bool onOurPage = entryTpl || claimBtn || pinkCount > 0 || popup;
                    if (!onOurPage)
                    {
                        string rec = _uidKnown && config.LastClaimByUid.TryGetValue(_currentUid, out var rd2) ? rd2 : "无记录";
                        _dbgAction = $"UID{(_uidKnown ? "…" + _currentUid[^4..] : "?")} 今日已领完(该账号记录:{rec})-静默不按ESC";
                        return;
                    }
                    if (inGrace) { _dbgAction = "已达上限-启动宽限中"; return; }
                    if (_step != FlowStep.Retreating)
                    {
                        _step = FlowStep.Retreating;
                        _retreatStart = now;
                        _returnEscCount = 0;
                        _nextReturnEscTime = now;
                        _lastActionTime = now;
                        _logService.LogInfo($"[活跃度] 该账号今日已达上限，开始撤退回主界面 [识别依据: {BuildEvidence(entryTpl, claimBtn, blueCount, pinkCount, progressCount, popup, banner)}]");
                    }
                }
            }

            // ④ 流程第1步：主界面（模板确认）→ 按F1；撤退中见到主界面 → 收工
            if (f1Valid)
            {
                if (_step == FlowStep.Retreating)
                {
                    _logService.LogInfo("[活跃度] 已确认回到主界面，撤退结束");
                    ResetFlowState();
                    _uidWantOcr = true;
                    _dbgAction = "撤退完成-已确认主界面";
                    return;
                }
                if (_step != FlowStep.Idle) ResetFlowState();
                if (inGrace) { _dbgAction = "启动宽限中-等待画面稳定"; return; }
                if (now < _recheckUntil) { _dbgAction = $"重查冷却中（至{_recheckUntil:HH:mm:ss}）"; return; }
                if (_uidOcrBusy || _uidWantOcr)
                {
                    _dbgAction = _uidOcrBusy ? "识别UID中-等待OCR结果后决定是否领取" : "UID识别排队";
                    return;
                }
                if (IsDuplicateClick("F1")) { _dbgAction = "F1防重复"; return; }
                _router.KeyPress(_hwnd, 0x70);
                _logService.LogInfo($"[键鼠] 进入探索指南 [识别依据: 主界面F1按钮模板 | UID={(_uidKnown ? _currentUid : "unknown")}判定今日未领完]");
                MarkClick("F1");
                _step = FlowStep.WaitGuide;
                _flowStartTime = now;
                _lastActionTime = now;
                _entryClickCount = 0;
                _blindF1Count = 0;
                _blindF1FirstSeen = DateTime.MinValue;
                _guideWaitUntil = now.AddMilliseconds(GuideWaitMs);
                _dbgAction = "按F1进探索指南";
                return;
            }

            // ⑤ 流程第1步兜底：盲按F1
            if (blindF1Eligible && _blindF1Count < BlindF1Max)
            {
                if (_uidOcrBusy || _uidWantOcr)
                {
                    _dbgAction = _uidOcrBusy ? "识别UID中-盲按F1暂缓" : "UID识别排队";
                    return;
                }
                if (_blindF1FirstSeen == DateTime.MinValue)
                {
                    _blindF1FirstSeen = now;
                    _dbgAction = "疑似主界面(F1模板未识别)-盲按F1待第2帧确认";
                    return;
                }
                if ((now - _blindF1FirstSeen).TotalMilliseconds >= BlindF1ConfirmMs
                    && (now - _lastClickTime).TotalMilliseconds >= BlindF1RetryMs)
                {
                    _router.KeyPress(_hwnd, 0x70);
                    _blindF1Count++;
                    _step = FlowStep.WaitGuide;
                    _flowStartTime = now;
                    _lastActionTime = now;
                    _entryClickCount = 0;
                    _guideWaitUntil = now.AddMilliseconds(GuideWaitMs);
                    _blindF1FirstSeen = DateTime.MinValue;
                    _logService.LogWarning($"[键鼠] 盲按F1进入探索指南（第{_blindF1Count}次）[识别依据: F1模板未命中且无任何页面元素,连续{BlindF1ConfirmMs}ms确认]");
                    _dbgAction = $"盲按F1(第{_blindF1Count}次)";
                    return;
                }
                _dbgAction = "盲按F1确认中";
                return;
            }
            if (!blindF1Eligible) _blindF1FirstSeen = DateTime.MinValue;

            // ⑥ 领取按钮
            if (claimBtn)
            {
                if (_step == FlowStep.Idle) { _step = FlowStep.InActivity; _flowStartTime = now; }
                if (!IsDuplicateClick("领取按钮"))
                {
                    var hit = _templates.MatchAny(frame, ClaimRoi1, GroupClaim)
                           ?? _templates.MatchAny(frame, ClaimRoi2, GroupClaim);
                    if (hit is null)                    // 修CS8629：两次匹配之间画面可能变化
                    {
                        _dbgAction = "领取按钮消失-本帧跳过";
                        return;
                    }
                    Rect r = hit.Value;
                    var (px, py) = _router.ClickLogical(_hwnd, r.X + r.Width / 2, r.Y + r.Height / 2);
                    _logService.LogInfo($"[键鼠] 活跃度领取奖励 [识别依据: 领取按钮模板]{_router.ClickTag(px, py, "左键")}");
                    MarkClick("领取按钮");
                    _lastActionTime = now;
                    _dbgAction = "点领取按钮";
                }
                else _dbgAction = "领取按钮防重复";
                return;
            }

            // ⑦ 蓝色礼盒=可领取 → 点礼盒中心
            if (firstBlue >= 0)
            {
                if (_step == FlowStep.Idle)
                {
                    _idleBlueStreak++;                  // 待命态需连续2帧确认（防加载界面颜色误报）
                    if (_idleBlueStreak < IdleBlueConfirmFrames)
                    {
                        _dbgAction = $"疑似蓝礼盒-待确认({_idleBlueStreak}/{IdleBlueConfirmFrames})";
                        return;
                    }
                    _step = FlowStep.InActivity;
                    _flowStartTime = now;
                }
                else _idleBlueStreak = 0;

                if (!IsDuplicateClick($"档位{firstBlue}"))
                {
                    Rect r = TierRois[firstBlue];
                    var (px, py) = _router.ClickLogical(_hwnd, r.X + r.Width / 2, r.Y + r.Height / 2);
                    _logService.LogInfo($"[键鼠] 领取活跃度[{(firstBlue + 1) * 20}档]礼盒 [识别依据: 蓝色礼盒颜色,可领{blueCount}个]{_router.ClickTag(px, py, "左键")}");
                    MarkClick($"档位{firstBlue}");
                    _lastActionTime = now;
                    _dbgAction = $"点{(firstBlue + 1) * 20}档蓝礼盒";
                }
                else _dbgAction = "蓝礼盒防重复";
                return;
            }
            else _idleBlueStreak = 0;                   // 本帧无蓝礼盒 → 计数清零

            // ⑧ 5个粉✔全可见 = 该账号全部领完 → 落盘
            if (pinkCount == TierRois.Length)
            {
                MarkDone(config, gameDay);
                _dbgAction = "5档全领完(粉✔x5)-已记录";
            }

            // ⑨ 流程第2步：点击每日活跃界面入口
            bool entryStepOk = _step == FlowStep.Idle || _step == FlowStep.WaitGuide || _step == FlowStep.EntryClicked;
            bool entryClickable = entryTpl || (entryBadge && (_step == FlowStep.WaitGuide || _step == FlowStep.EntryClicked));
            if (entryClickable && entryStepOk)
            {
                if (_step == FlowStep.WaitGuide && now < _guideWaitUntil)
                {
                    _dbgAction = "等待指南页打开后点入口";
                    return;
                }
                if (_step == FlowStep.Idle)
                {
                    if (now < _recheckUntil) { _dbgAction = "入口可见但重查冷却中-等待"; return; }
                    if (!IsDuplicateClick("入口"))
                    {
                        int cx = EntryRoi.X + EntryRoi.Width / 2;
                        int cy = EntryRoi.Y + EntryRoi.Height / 2;
                        var (px, py) = _router.ClickLogical(_hwnd, cx, cy);
                        _step = FlowStep.EntryClicked;
                        _flowStartTime = now;
                        _entryClickCount = 1;
                        _lastEntryClickTime = now;
                        _lastActionTime = now;
                        _lastProgressMask = -1;
                        _logService.LogInfo($"[键鼠] 点击活跃度（接管已打开的指南页）[识别依据: 入口模板]{_router.ClickTag(px, py, "左键")}");
                        MarkClick("入口");
                    }
                    else _dbgAction = "入口防重复";
                    return;
                }
                if (_entryClickCount < EntryClickMax && now >= _recheckUntil
                    && (now - _lastEntryClickTime).TotalMilliseconds >= EntryRetryMs
                    && !IsDuplicateClick("入口"))
                {
                    int cx = EntryRoi.X + EntryRoi.Width / 2;
                    int cy = EntryRoi.Y + EntryRoi.Height / 2;
                    var (px, py) = _router.ClickLogical(_hwnd, cx, cy);
                    _entryClickCount++;
                    _lastEntryClickTime = now;
                    _step = FlowStep.EntryClicked;
                    _lastActionTime = now;
                    _lastProgressMask = -1;
                    string basis = entryTpl ? "入口模板" : "红点角标颜色";
                    _logService.LogInfo(_entryClickCount == 1
                        ? $"[键鼠] 点击活跃度 [识别依据: {basis}]{_router.ClickTag(px, py, "左键")}"
                        : $"[键鼠] 入口未跳转，重试第{_entryClickCount}次 [识别依据: {basis}]{_router.ClickTag(px, py, "左键")}");
                    MarkClick("入口");
                    _dbgAction = $"点入口(第{_entryClickCount}次,依据:{basis})";
                    return;
                }
                _dbgAction = _entryClickCount >= EntryClickMax
                    ? $"入口已点{_entryClickCount}次无效-等待撤退"
                    : $"入口重试间隔等待（已点{_entryClickCount}次）";
            }

            // ⑩ 盲点入口：仅在 已按F1/已点入口 且 从未确认进入活跃度页 时允许
            if ((_step == FlowStep.WaitGuide || _step == FlowStep.EntryClicked)
                && _entryClickCount < EntryClickMax
                && !f1Raw && !entryTpl && !badgeSeen
                && blueCount == 0 && pinkCount == 0 && progressCount == 0
                && (now - _flowStartTime).TotalMilliseconds > BlindEntryDelayMs
                && now >= _recheckUntil
                && (now - _lastEntryClickTime).TotalMilliseconds >= EntryRetryMs
                && !IsDuplicateClick("盲点入口"))
            {
                int cx = EntryRoi.X + EntryRoi.Width / 2;
                int cy = EntryRoi.Y + EntryRoi.Height / 2;
                var (px, py) = _router.ClickLogical(_hwnd, cx, cy);
                _entryClickCount++;
                _lastEntryClickTime = now;
                _step = FlowStep.EntryClicked;
                _lastActionTime = now;
                _logService.LogWarning($"[键鼠] 盲点活跃度入口（第{_entryClickCount}次）[识别依据: 模板+红点均未命中,固定坐标兜底]{_router.ClickTag(px, py, "左键")}");
                MarkClick("盲点入口");
                _dbgAction = $"盲点入口(第{_entryClickCount}次)";
                return;
            }

            // ⑪ 进度条记录
            if (_step == FlowStep.InActivity && pMask != _lastProgressMask)
            {
                if (pMask > 0 || _lastProgressMask > 0)
                {
                    string marks = "";
                    int reached = 0;
                    for (int i = 0; i < ProgressRois.Length; i++)
                    {
                        bool ok = (pMask & (1 << i)) != 0;
                        marks += $"[{(i + 1) * 20}]{(ok ? "✔" : "✘")} ";
                        if (ok) reached++;
                    }
                    _logService.LogInfo($"[记录] 当前活跃度完成多少 {marks}| 活跃度={reached * 20}");
                    _dbgAction = "记录进度条";
                }
                _lastProgressMask = pMask;
            }

            // ⑫ 状态机看门狗：熔断 / 撤退启动 / 撤退中ESC节奏
            if (_step == FlowStep.Idle) return;

            if (_step == FlowStep.Retreating)
            {
                if ((now - _retreatStart).TotalSeconds > RetreatingFuseSec)
                {
                    _logService.LogWarning($"[活跃度] 撤退超过{RetreatingFuseSec}秒未确认回主界面-停止ESC，等待重查");
                    _recheckUntil = now.AddMinutes(RecheckIntervalMin);
                    ResetFlowState();
                    _uidWantOcr = true;
                    return;
                }
            }
            else if ((now - _flowStartTime).TotalSeconds > FlowMaxSec)
            {
                _logService.LogWarning($"[活跃度] 流程超过{FlowMaxSec}秒未结束，保险丝熔断，今日放弃（明天重置后重试）");
                _giveUpGameDay = gameDay;
                ResetFlowState();
                _uidWantOcr = true;
                return;
            }

            // 撤退启动：活跃度页无可领
            if (_step == FlowStep.InActivity)
            {
                bool claimables = claimBtn || blueCount > 0;
                if (!claimables && (now - _lastActionTime).TotalSeconds > IdleEscActivitySec)
                {
                    bool evidence = entryTpl || claimBtn || blueCount > 0 || pinkCount > 0 || progressCount > 0;
                    _recheckUntil = now.AddMinutes(RecheckIntervalMin);
                    if (evidence)
                    {
                        _logService.LogInfo($"[活跃度] 当前无可领取档位（粉{pinkCount}/5 蓝{blueCount}），撤退回主界面，{RecheckIntervalMin}分钟后重查（等活跃度上涨）[识别依据: {BuildEvidence(entryTpl, claimBtn, blueCount, pinkCount, progressCount, popup, banner)}]");
                        _step = FlowStep.Retreating;
                        _retreatStart = now;
                        _returnEscCount = 0;
                        _nextReturnEscTime = now;
                        _lastActionTime = now;
                    }
                    else
                    {
                        _logService.LogInfo($"[活跃度] 画面无可识别流程元素，疑似已离开活跃度页 → 不按ESC，复位等待{RecheckIntervalMin}分钟后重查 [本帧识别: {BuildFullSnapshot(f1Raw, entryTpl, badgeSeen, claimBtn, blueCount, pinkCount, progressCount, popup, banner)}]");
                        ResetFlowState();
                        _uidWantOcr = true;
                        return;
                    }
                }
            }

            // 撤退启动：入口多次点击未跳转
            if ((_step == FlowStep.WaitGuide || _step == FlowStep.EntryClicked)
                && _entryClickCount >= EntryClickMax
                && (now - _lastActionTime).TotalSeconds > IdleEscSec)
            {
                _recheckUntil = now.AddMinutes(RecheckIntervalMin);
                _logService.LogInfo($"[活跃度] 入口已点{_entryClickCount}次未跳转，撤退回主界面，{RecheckIntervalMin}分钟后重查");
                _step = FlowStep.Retreating;
                _retreatStart = now;
                _returnEscCount = 0;
                _nextReturnEscTime = now;
                _lastActionTime = now;
            }

            // 撤退中：有证据才ESC，直到F1模板确认回主界面
            if (_step == FlowStep.Retreating)
            {
                bool evidence = popup || banner || entryTpl || claimBtn || blueCount > 0 || pinkCount > 0 || progressCount > 0;
                if (evidence)
                {
                    if (now >= _nextReturnEscTime && !IsDuplicateClick("ESC撤退"))
                    {
                        _router.KeyPress(_hwnd, 0x1B);
                        _returnEscCount++;
                        _nextReturnEscTime = _returnEscCount < ReturnEscMax
                            ? now.AddSeconds(ReturnEscIntervalSec)
                            : now.AddSeconds(ReturnEscSlowSec);
                        _lastActionTime = now;
                        _logService.LogInfo($"[键鼠] 按Esc返回主界面（第{_returnEscCount}次）[撤退中 | 识别依据: {BuildEvidence(entryTpl, claimBtn, blueCount, pinkCount, progressCount, popup, banner)}]");
                        _dbgAction = $"撤退ESC第{_returnEscCount}次(依据见日志)";
                    }
                    else _dbgAction = "撤退中-ESC节奏等待";
                }
                else
                {
                    if ((now - _retreatStart).TotalSeconds > RetreatAmbiguousGiveUpSec)
                    {
                        _logService.LogInfo($"[活跃度] 撤退中画面已无可识别元素-停止ESC（若已在大世界则无影响）[本帧识别: {BuildFullSnapshot(f1Raw, entryTpl, badgeSeen, claimBtn, blueCount, pinkCount, progressCount, popup, banner)}]");
                        ResetFlowState();
                        _uidWantOcr = true;
                    }
                    else _dbgAction = "撤退中-画面过渡,等待证据";
                }
            }
            else if (_dbgAction == "无")
            {
                _dbgAction = "流程运行中-等待画面响应";
            }
        }
    }
}
