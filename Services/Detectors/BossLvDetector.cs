using System;
using System.IO;
using System.Text.Json;
using OpenCvSharp;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Detectors
{
    /// <summary>
    /// 周本BOSS验证器（头顶 LV 血条模板匹配）：
    /// ROI 内匹配 Assets\NTE-CombatYOLO\Boss_Lv.bmp，≥阈值判定周本BOSS在场。
    /// 配置：exe 旁 boss_lv_roi.json（没有则用内置标定值 721,17,242,53）。
    /// </summary>
    public sealed class BossLvDetector : IDisposable
    {
        private readonly ILogService _log;
        private readonly string _tplPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "Assets", "NTE-CombatYOLO", "Boss_Lv.bmp");
        private readonly string _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "boss_lv_roi.json");

        private sealed class RoiConfig
        {
            public int RoiX { get; set; } = 721;        // ← 你标的 721,17 为左上角
            public int RoiY { get; set; } = 17;
            public int RoiWidth { get; set; } = 242;    // 963-721
            public int RoiHeight { get; set; } = 53;    // 70-17
            public double MatchThreshold { get; set; } = 0.75;
        }

        private RoiConfig _cfg = new();
        private string _source = "内置标定默认值";
        private Mat? _tpl;

        public double LastMatch { get; private set; }   // 最近一次匹配度 0~1（诊断行取证用）
        public bool LastHit { get; private set; }       // 是否≥阈值
        private Rect _lastRect;                         // 命中位置（画框用）
        public Rect LastRect => _lastRect;              // 触发器拿去当目标框

        public BossLvDetector(ILogService log)
        {
            _log = log;
            LoadConfig();
            LoadTemplate();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_jsonPath))
                {
                    var cfg = JsonSerializer.Deserialize<RoiConfig>(File.ReadAllText(_jsonPath));
                    if (cfg != null) { _cfg = cfg; _source = "boss_lv_roi.json"; }
                }
            }
            catch (Exception ex) { _log.LogDebug($"[BOSS·LV] 配置读取失败，用内置默认: {ex.Message}"); }
            _log.LogInfo($"[BOSS·LV] 上线 | ROI=({_cfg.RoiX},{_cfg.RoiY},{_cfg.RoiWidth},{_cfg.RoiHeight}) | 匹配≥{_cfg.MatchThreshold:F2} | 来源={_source}");
        }

        private void LoadTemplate()
        {
            try
            {
                if (!File.Exists(_tplPath)) { _log.LogError($"[BOSS·LV] 模板不存在: {_tplPath}"); return; }
                _tpl = Cv2.ImRead(_tplPath, ImreadModes.Color);
                if (_tpl.Empty()) { _log.LogError($"[BOSS·LV] 模板解码失败: {_tplPath}"); _tpl.Dispose(); _tpl = null; return; }
                _log.LogInfo($"[BOSS·LV] 模板加载成功 {_tpl.Width}x{_tpl.Height}");
            }
            catch (Exception ex) { _log.LogError($"[BOSS·LV] 模板加载异常: {ex.Message}"); }
        }

        /// <summary>每帧调用：ROI 内模板匹配，结果存 LastMatch / LastHit。</summary>
        public void Analyze(Mat frame)
        {
            LastMatch = 0; LastHit = false;
            if (_tpl == null || frame.Empty()) return;

            int x = Math.Clamp(_cfg.RoiX, 0, frame.Width - 1);
            int y = Math.Clamp(_cfg.RoiY, 0, frame.Height - 1);
            int w = Math.Min(_cfg.RoiWidth, frame.Width - x);
            int h = Math.Min(_cfg.RoiHeight, frame.Height - y);
            if (w <= 0 || h <= 0 || w < _tpl.Width || h < _tpl.Height)
            {
                _log.LogDebug($"[BOSS·LV] ROI({w}x{h}) 装不下模板({_tpl.Width}x{_tpl.Height})，检查分辨率是否 1920x1080");
                return;
            }

            using var roi = new Mat(frame, new Rect(x, y, w, h));
            using var res = new Mat();
            Cv2.MatchTemplate(roi, _tpl, res, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out Point maxLoc);

            LastMatch = maxVal;
            LastHit = maxVal >= _cfg.MatchThreshold;
            _lastRect = new Rect(x + maxLoc.X, y + maxLoc.Y, _tpl.Width, _tpl.Height);
        }

        /// <summary>视觉调试画框：绿=命中，灰=未命中。</summary>
        public void DrawDebug(Mat frame)
        {
            if (_tpl == null) return;
            var color = LastHit ? Scalar.LimeGreen : Scalar.Gray;
            Cv2.Rectangle(frame, _lastRect, color, 2);
            Cv2.PutText(frame, $"BOSS {LastMatch:F2}", new Point(_lastRect.X, Math.Max(14, _lastRect.Y - 6)),
                HersheyFonts.HersheySimplex, 0.55, color, 1);
        }

        public void Dispose() => _tpl?.Dispose();
    }
}
