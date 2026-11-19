using System;
using System.IO;
using System.Text.Json;
using OpenCvSharp;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Triggers
{
    /// <summary>
    /// Q 能量就绪检测（环形量表法）—— 与 Tests 标定工具同一算法，数据互通。
    /// 原理：Q 图标外圈是能量量表环 → 取环形带（半径 0.70~0.98）统计高亮像素占比；
    ///       标定实测满能≈42%、空能≈0 → 占比达阈值且连续 N 帧判就绪。
    /// ROI 来源：优先读 qenergy_roi.json（Tests 标定工具输出，放 exe 旁即生效），
    ///           没有则用内置标定默认值 (1764,912,118,118)。
    /// </summary>
    public sealed class QEnergyDetector : IDisposable
    {
        // ===== 内置默认 = Tests 标定工具实测值（JSON 缺失时兜底） =====
        private sealed class RoiConfig
        {
            public int RoiX { get; set; } = 1757;
            public int RoiY { get; set; } = 915;
            public int RoiWidth { get; set; } = 112;
            public int RoiHeight { get; set; } = 109;
            public bool AnchorRight { get; set; } = true;   // 帧宽≠1920 时贴右缘
            public int ExcludeBottomPx { get; set; } = 22;  // 掐掉环底部 Q 键帽遮挡
            public byte BrightMinV { get; set; } = 190;     // HSV V ≥ 190 记亮像素
            public double BrightReadyRatio { get; set; } = 0.15; // 满能42% 空能≈0，实测值
            public int ConfirmFrames { get; set; } = 2;     // 连续2帧，防特效闪光误判
        }

        private const double CastCooldownSeconds = 1.5;
        private static readonly string[] ConfigProbePaths =
        {
            "qenergy_roi.json",
            Path.Combine(AppContext.BaseDirectory, "qenergy_roi.json"),
        };

        private readonly ILogService _log;
        private readonly RoiConfig _cfg;
        private readonly Mat _hsv = new();
        private readonly Mat _brightMask = new();
        private readonly Mat _ringBand;  

        private double _lastBright;
        private int _hitStreak;
        private bool _ready;
        private DateTime _castCooldownUntil = DateTime.MinValue;

        public Rect LastRoi { get; private set; }
        public bool IsReady => _ready;
        public bool InCastCooldown => DateTime.Now < _castCooldownUntil;
        public double LastBright => _lastBright;

        public QEnergyDetector(ILogService log)
        {
            _log = log;
            _cfg = LoadConfig();
            // ★ 修复点：环形带遮罩必须在构造时按 ROI 尺寸分配实体。
            //   空壳 Mat 上 SetTo/Circle 是静默无操作，BitwiseAnd 会因尺寸不符炸出
            //   "The operation is neither 'array op array'..." —— Tests 工具原有此分配，重建时不可丢。
            _ringBand = new Mat(_cfg.RoiHeight, _cfg.RoiWidth, MatType.CV_8UC1);

            bool fromJson = File.Exists(ConfigProbePaths[0]) || File.Exists(ConfigProbePaths[1]);
            _log.LogInfo($"[Q能量] 上线 | ROI=({_cfg.RoiX},{_cfg.RoiY},{_cfg.RoiWidth},{_cfg.RoiHeight})" +
                         $" | V≥{_cfg.BrightMinV} 占比≥{_cfg.BrightReadyRatio:P0} 连续{_cfg.ConfirmFrames}帧" +
                         $" | 来源={(fromJson ? "qenergy_roi.json" : "内置标定默认值")}");
        }

        private static RoiConfig LoadConfig()
        {
            foreach (var p in ConfigProbePaths)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var cfg = JsonSerializer.Deserialize<RoiConfig>(File.ReadAllText(p));
                    if (cfg != null && cfg.RoiWidth > 4 && cfg.RoiHeight > 4) return cfg;
                }
                catch { /* 坏文件走默认 */ }
            }
            return new RoiConfig();
        }

        public void NotifyCast() => _castCooldownUntil = DateTime.Now.AddSeconds(CastCooldownSeconds);

        public void Analyze(Mat frame)
        {
            if (frame == null || frame.Empty()) { ResetReady(); return; }
            try
            {
                int cw = frame.Width, ch = frame.Height;
                if (cw < _cfg.RoiWidth || ch < _cfg.RoiHeight) { ResetReady(); return; }

                LastRoi = ComputeRoi(cw, ch);
                using var roi = new Mat(frame, LastRoi);
                Cv2.CvtColor(roi, _hsv, ColorConversionCodes.BGR2HSV);

                // ① 高亮像素（量表环发光）
                Cv2.InRange(_hsv, new Scalar(0, 0, _cfg.BrightMinV), new Scalar(180, 255, 255), _brightMask);

                // ② 环形带遮罩：只看量表环，不看图标本体
                _ringBand.SetTo(0);
                int cx = _cfg.RoiWidth / 2, cy = _cfg.RoiHeight / 2;
                int r = Math.Min(_cfg.RoiWidth, _cfg.RoiHeight) / 2;
                Cv2.Circle(_ringBand, cx, cy, (int)(r * 0.98), Scalar.All(255), -1);
                Cv2.Circle(_ringBand, cx, cy, (int)(r * 0.70), Scalar.All(0), -1);
                if (_cfg.ExcludeBottomPx > 0)
                    Cv2.Rectangle(_ringBand,
                        new Rect(0, _cfg.RoiHeight - _cfg.ExcludeBottomPx, _cfg.RoiWidth, _cfg.ExcludeBottomPx),
                        Scalar.All(0), -1);

                // ③ 环带内亮占比
                Cv2.BitwiseAnd(_brightMask, _ringBand, _brightMask);
                int ringArea = Cv2.CountNonZero(_ringBand);
                _lastBright = ringArea > 0 ? Cv2.CountNonZero(_brightMask) / (double)ringArea : 0;

                // ④ 连续 N 帧确认
                if (_lastBright >= _cfg.BrightReadyRatio)
                {
                    _hitStreak++;
                    if (!_ready && _hitStreak >= _cfg.ConfirmFrames)
                    {
                        _ready = true;
                        _log.LogDebug($"[Q能量] 就绪 (环亮:{_lastBright:P0})");
                    }
                }
                else ResetReady();
            }
            catch (Exception ex)
            {
                _log.LogError($"[Q能量] Analyze 异常: {ex.Message}");
                ResetReady();
            }
        }

        private void ResetReady() { _ready = false; _hitStreak = 0; }

        private Rect ComputeRoi(int cw, int ch)
        {
            int x = _cfg.AnchorRight ? _cfg.RoiX + (cw - 1920) : _cfg.RoiX;
            x = Math.Clamp(x, 0, cw - _cfg.RoiWidth);
            int y = Math.Clamp(_cfg.RoiY, 0, ch - _cfg.RoiHeight);
            return new Rect(x, y, _cfg.RoiWidth, _cfg.RoiHeight);
        }

        public void DrawDebug(Mat frame)
        {
            if (frame == null || frame.Empty()) return;
            try
            {
                LastRoi = ComputeRoi(frame.Width, frame.Height);   // 没分析过也画框，预览永远对位
                var color = _ready ? Scalar.LimeGreen : (InCastCooldown ? Scalar.Orange : Scalar.Gray);
                Cv2.Rectangle(frame, LastRoi, color, 2);
                var tag = InCastCooldown ? " CD" : (_ready ? " READY" : "");
                Cv2.PutText(frame, $"Q {_lastBright:P0}{tag}",
                    new Point(LastRoi.X, Math.Max(14, LastRoi.Y - 6)),
                    HersheyFonts.HersheySimplex, 0.55, color, 1);
            }
            catch { /* 调试绘制不影响主流程 */ }
        }

        public void Dispose() { _hsv.Dispose(); _brightMask.Dispose(); _ringBand.Dispose(); }
    }
}
