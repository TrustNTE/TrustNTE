using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TrustNTE.Helpers;
using TrustNTE.Models;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Triggers
{
    public class AutoLootTrigger : ITaskTrigger
    {
        public string Name => "自动拾取";
        public bool IsEnabled { get; set; } = false;
        public Func<int, int, (int, int)>? CoordMapper { get; set; }

        private readonly IConfigService _configService;
        private readonly IInputRouter _router;
        private readonly ILogService _log;
        private readonly IntPtr _hwnd;
        private readonly NteWordFTemplateService _wordF;
        private readonly Func<Mat, Task<string?>> _ocrRecognize;

        private long _lastOcrMs = 0;
        private DateTime _lastPressUtc = DateTime.MinValue;
        private const int CooldownMs = 1000;

        private static readonly Rect SearchArea = new(0, 540, 1920, 540);
        private static readonly Rect TextRoi = new(1179, 568, 400, 41);
        private static readonly string[] SafeExclusions = { "聊天", "设置", "前往", "传送" };

        // ★ 大牛工具：内容变化检测
        private readonly ChangeDetector<string> _textChange = new();

        public AutoLootTrigger(IConfigService configService, IInputRouter router, ILogService log,
            IntPtr hwnd, NteWordFTemplateService wordF, Func<Mat, Task<string?>> ocrRecognize)
        {
            _configService = configService; _router = router; _log = log;
            _hwnd = hwnd; _wordF = wordF; _ocrRecognize = ocrRecognize;
        }

        public void OnCapture(Mat resizedMat, double scale)
        {
            var cfg = _configService.Load();
            if (!cfg.AutoLootEnabled) return;

            if ((DateTime.UtcNow - _lastPressUtc).TotalMilliseconds < CooldownMs) return;

            // 分支 1：模板模式
            if (cfg.AutoLootEngine == "模板")
            {
                var hit = _wordF.MatchAnyAcrossAllGroups(resizedMat, SearchArea, out var matchedName);
                if (hit is null) return;
                Press(cfg, $"模板命中[{matchedName}]");
                return;
            }

            // 分支 2：OCR 模式
            long now = Environment.TickCount64;
            if (now - _lastOcrMs < 300) return; // 300ms 节流
            _lastOcrMs = now;

            if (TextRoi.Right > resizedMat.Width || TextRoi.Bottom > resizedMat.Height) return;

            using var strip = new Mat(resizedMat, TextRoi);

            long t0 = Environment.TickCount64;
            string? raw;
            try
            {
                raw = _ocrRecognize(strip).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _log.LogError($"[自动拾取] OCR 异常: {ex.Message}");
                return;
            }

            long costMs = Environment.TickCount64 - t0;
            string text = (raw ?? "").Replace(" ", "").Trim();

            // ★ 大牛方案：判断内容是否发生变化
            bool changed = _textChange.IsChanged(text);

            // 诊断日志：只有文本变化时才记录，且降级为 Debug（玩家默认看不到）
            if (text.Length > 0 && changed)
                _log.LogDebug($"[自动拾取][校准] OCR({costMs}ms)=\"{text}\"");

            // 防瞎编（纯英文字母丢弃）
            if (text.All(char.IsLetter) && text.All(c => c < 128)) return;

            if (text.Length < 2) return;

            // 决策日志：如果被名单拦截，打一次 Info 给玩家看
            if (!ShouldPick(text, cfg.AutoLootListMode, cfg.AutoLootWhitelistText, cfg.AutoLootBlacklistText))
            {
                if (text.Length > 0 && changed)
                    _log.LogInfo($"[自动拾取] 发现「{text}」（名单未放行）");
                return;
            }

            // 走到这 = 通过名单 → Press 里面会打按键 Info 日志
            Press(cfg, $"OCR命中「{text}」");
        }

        // ★ 修复：判定权收归 PickupListEvaluator（模式裁决制）。
        // 旧实现白名单模式里有一行黑名单一票否决 —— 同名词跨名单时白名单模式永远不拾取，
        // 即"切哪个模式都不交互"的根因。现行为：
        //   - 白名单模式只查白名单，黑名单模式只查黑名单（互不偷看）
        //   - 归一化比对（全角/大小写/空白）
        //   - 保留 Contains 子串语义（OCR 文本可能混入键帽字符，如 "F驾驶"）
        //   - 保留 SafeExclusions 安全豁免（传送/菜单类提示绝不代按 F，与名单无关）
        // 保留原签名：调用点与单元测试零改动
        internal static bool ShouldPick(string text, string listMode, string? whiteRaw, string? blackRaw)
        {
            foreach (var ex in SafeExclusions)
                if (text.Contains(ex)) return false;

            return PickupListEvaluator.ShouldPick(
                text,
                blacklistMode: listMode == "黑名单模式",
                PickupListEvaluator.ParseLines(whiteRaw),
                PickupListEvaluator.ParseLines(blackRaw));
        }

        private void Press(AppConfig cfg, string reason)
        {
            _router.KeyPress(_hwnd, ToVirtualKey(cfg.AutoLootKey));
            _lastPressUtc = DateTime.UtcNow;
            _log.LogInfo($"[自动拾取] {reason} → 按下 {cfg.AutoLootKey} 键");
        }

        private static int ToVirtualKey(string? keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName)) return 0x46;
            keyName = keyName.Trim().ToUpperInvariant();
            if (keyName is "SPACE" or "空格") return 0x20;
            if (keyName.Length == 1)
            {
                char c = keyName[0];
                if (c is >= 'A' and <= 'Z') return 0x41 + (c - 'A');
                if (c is >= '0' and <= '9') return 0x30 + (c - '0');
            }
            return 0x46;
        }

        public void DrawDebug(Mat drawMat)
        {
            if (!IsEnabled) return;
            if (_configService.Load().AutoLootEngine == "模板")
                Cv2.Rectangle(drawMat, SearchArea, new Scalar(0, 255, 0), 1);
            else
                Cv2.Rectangle(drawMat, TextRoi, new Scalar(0, 255, 0), 1);
        }
    }
}
