using System;
using OpenCvSharp;

namespace TrustNTE.Tests.Tools.ActivityProgress
{
    /// <summary>
    /// 活跃度进度条检测器（v4：按列长度判定）。
    /// 先提取青色掩膜，再按"列"统计：
    ///   一列中青色像素数 ≥ MinColHits → 该列算已填充。
    ///   填充率 = 已填充列数 / 总列数（纯长度，与 ROI 高度无关）。
    /// </summary>
    public sealed class ActivityDetector : IDisposable
    {
        private readonly ActivityConfig _cfg;
        private readonly Mat?[] _hsvs;
        private readonly Mat?[] _masks;
        private readonly int[] _hitStreaks;

        public ActivityDetector(ActivityConfig cfg)
        {
            _cfg = cfg;
            _hsvs = new Mat?[cfg.Rois.Count];
            _masks = new Mat?[cfg.Rois.Count];
            _hitStreaks = new int[cfg.Rois.Count];
        }

        /// <summary>最近一次 Analyze 使用的实际 ROI 矩形</summary>
        public Rect[] LastRois { get; private set; } = Array.Empty<Rect>();

        /// <summary>单段进度条的检测结果</summary>
        public sealed record SegmentResult(
            int Milestone,     // 该段填满对应的档位
            double FillRatio,  // 已填充列占比 0~1
            int FillCols,      // 已填充列数（调试用）
            bool Reached,      // 本帧判定：该段已填满
            bool Confirmed);   // 连续命中后的确认判定

        /// <summary>一帧的完整检测结果</summary>
        public sealed record ActivityResult(
            SegmentResult[] Segments,
            int MaxConfirmed,
            int EstimatedValue)
        {
            public static readonly ActivityResult Empty = new(Array.Empty<SegmentResult>(), 0, 0);
        }

        public ActivityResult Analyze(Mat normalizedFrame)
        {
            int cw = normalizedFrame.Width;
            int ch = normalizedFrame.Height;
            if (cw < 1920 || ch < 1080) return ActivityResult.Empty;

            LastRois = new Rect[_cfg.Rois.Count];
            var segments = new SegmentResult[_cfg.Rois.Count];
            int maxConfirmed = 0;

            for (int i = 0; i < _cfg.Rois.Count; i++)
            {
                var r = _cfg.Rois[i];

                int x = Math.Clamp(r.X, 0, cw - r.W);
                int y = Math.Clamp(r.Y, 0, ch - r.H);
                LastRois[i] = new Rect(x, y, r.W, r.H);

                using var roi = new Mat(normalizedFrame, LastRois[i]);

                _hsvs[i] ??= new Mat(r.H, r.W, MatType.CV_8UC3);
                Cv2.CvtColor(roi, _hsvs[i]!, ColorConversionCodes.BGR2HSV);

                // 提取青色填充掩膜
                _masks[i] ??= new Mat(r.H, r.W, MatType.CV_8UC1);
                Cv2.InRange(_hsvs[i]!,
                            new Scalar(_cfg.HMin, _cfg.SMin, _cfg.VMin),
                            new Scalar(_cfg.HMax, 255, 255),
                            _masks[i]!);

                // ══ 核心：按列统计长度（而不是面积）══
                double fillRatio;
                int filledCols = 0;

                if (_masks[i]!.GetArray(out byte[] px))
                {
                    int need = Math.Min(_cfg.MinColHits, r.H);
                    var colHits = new int[r.W];

                    // 逐行累加每列的青色像素数
                    for (int row = 0; row < r.H; row++)
                    {
                        int off = row * r.W;
                        for (int col = 0; col < r.W; col++)
                            if (px[off + col] != 0) colHits[col]++;
                    }

                    // 统计达标的列数
                    for (int col = 0; col < r.W; col++)
                        if (colHits[col] >= need) filledCols++;

                    fillRatio = (double)filledCols / r.W;
                }
                else
                {
                    // 兜底：拿不到像素数据时退回面积法
                    fillRatio = Cv2.CountNonZero(_masks[i]!) / (double)(r.W * r.H);
                }

                bool reached = fillRatio >= _cfg.FullRatio;
                _hitStreaks[i] = reached ? _hitStreaks[i] + 1 : 0;
                bool confirmed = reached && _hitStreaks[i] >= _cfg.ConfirmFrames;

                segments[i] = new SegmentResult(r.Milestone, fillRatio, filledCols, reached, confirmed);
                if (confirmed && r.Milestone > maxConfirmed)
                    maxConfirmed = r.Milestone;
            }

            // ── 反算当前活跃度 ──
            int estimated = 0;
            foreach (var seg in segments)
            {
                if (seg.Reached)
                    estimated = seg.Milestone;                          // 满 → 推进到档位
                else
                {
                    estimated += (int)Math.Round(seg.FillRatio * 20);   // 部分 → 累加
                    break;
                }
            }

            return new ActivityResult(segments, maxConfirmed, estimated);
        }

        /// <summary>获取某段的青色掩膜快照（BGR 三通道便于 ImShow）</summary>
        public Mat? SnapshotMask(int index)
        {
            if (index < 0 || index >= _masks.Length) return null;
            var m = _masks[index];
            if (m == null || m.IsDisposed) return null;

            var bgr = new Mat();
            Cv2.CvtColor(m, bgr, ColorConversionCodes.GRAY2BGR);
            return bgr;
        }

        /// <summary>把 5 段掩膜横向拼接成一条预览图</summary>
        public Mat? SnapshotMaskStrip(int targetHeight = 30)
        {
            int count = _cfg.Rois.Count;
            var parts = new Mat?[count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    using var snap = SnapshotMask(i);
                    if (snap != null && !snap.Empty())
                    {
                        double scale = (double)targetHeight / snap.Height;
                        int newW = Math.Max(1, (int)(snap.Width * scale));
                        var resized = new Mat();
                        Cv2.Resize(snap, resized, new OpenCvSharp.Size(newW, targetHeight));
                        parts[i] = resized;
                    }
                    else
                    {
                        parts[i] = new Mat(targetHeight, 60, MatType.CV_8UC3, Scalar.Black);
                    }
                }

                var nonNull = new Mat[count];
                for (int i = 0; i < count; i++) nonNull[i] = parts[i]!;

                var strip = new Mat();
                Cv2.HConcat(nonNull, strip);
                return strip;
            }
            finally
            {
                foreach (var p in parts) p?.Dispose();
            }
        }

        public void Dispose()
        {
            foreach (var m in _hsvs) m?.Dispose();
            foreach (var m in _masks) m?.Dispose();
        }
    }
}
