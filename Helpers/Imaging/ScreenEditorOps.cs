using System;
using OpenCvSharp;

namespace TrustNTE.Helpers
{
    /// <summary>
    /// 截图器纯 OpenCV 算子(按规范:图像处理归 Helpers\Imaging)。
    /// 除 FloodFill 原地修改外,一律不改入参;返回新 Mat 的调用方负责释放(红线3)。
    /// 注意:所有输出转回 BGR 3 通道 —— ImageHelper.MatToBitmapSource 对灰度图会错乱(像素格式硬编码 Bgra32)。
    /// </summary>
    public static class ScreenEditorOps
    {
        /// <summary>吸管:取一点 BGR。</summary>
        public static (byte r, byte g, byte b) PickColor(Mat bgr, int x, int y)
        {
            x = Math.Clamp(x, 0, bgr.Cols - 1);
            y = Math.Clamp(y, 0, bgr.Rows - 1);
            var v = bgr.At<Vec3b>(y, x);
            return (v.Item2, v.Item1, v.Item0);
        }

        /// <summary>油漆桶:种子填充,种子色 ± tolerance(固定范围)。原地修改,返回是否填充。</summary>
        public static bool FloodFill(Mat bgr, int x, int y, byte r, byte g, byte b, int tolerance)
        {
            if (bgr.Empty()) return false;
            x = Math.Clamp(x, 0, bgr.Cols - 1);
            y = Math.Clamp(y, 0, bgr.Rows - 1);
            var seed = bgr.At<Vec3b>(y, x);
            // 种子==填充色时 OpenCV 会异常,提前拦截
            if (seed.Item0 == b && seed.Item1 == g && seed.Item2 == r) return false;
            var lo = new Scalar(Math.Max(0, seed.Item0 - tolerance), Math.Max(0, seed.Item1 - tolerance), Math.Max(0, seed.Item2 - tolerance));
            var hi = new Scalar(Math.Min(255, seed.Item0 + tolerance), Math.Min(255, seed.Item1 + tolerance), Math.Min(255, seed.Item2 + tolerance));
            try
            {
                Cv2.FloodFill(bgr, new Point(x, y), new Scalar(b, g, r),
                    out _, lo, hi, FloodFillFlags.Link4 | FloodFillFlags.FixedRange);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 自动剪裁:四角(内缩2px)颜色在容差内视为背景色,裁出前景包围盒。
        /// 四角不同色返回 null(无效);找不到前景也返回 null。
        /// </summary>
        public static Mat? AutoCrop(Mat src, int tolerance)
        {
            if (src.Empty()) return null;
            int w = src.Cols, h = src.Rows;
            if (w < 8 || h < 8) return null;
            var c1 = src.At<Vec3b>(2, 2);
            var c2 = src.At<Vec3b>(2, w - 3);
            var c3 = src.At<Vec3b>(h - 3, 2);
            var c4 = src.At<Vec3b>(h - 3, w - 3);
            if (!Similar(c1, c2, tolerance) || !Similar(c1, c3, tolerance) || !Similar(c1, c4, tolerance))
                return null;

            using var bg = new Mat(src.Rows, src.Cols, src.Type(), new Scalar(c1.Item0, c1.Item1, c1.Item2));
            using var diff = new Mat();
            Cv2.Absdiff(src, bg, diff);
            using var gray = new Mat();
            Cv2.CvtColor(diff, gray, ColorConversionCodes.BGR2GRAY);
            using var mask = new Mat();
            Cv2.Threshold(gray, mask, tolerance, 255, ThresholdTypes.Binary);
            Cv2.MedianBlur(mask, mask, 3); // 去孤立噪点,防包围盒被撑大
            var rect = Cv2.BoundingRect(mask);
            if (rect.Width < 2 || rect.Height < 2) return null;
            return src[rect].Clone();
        }

        /// <summary>灰度二值化(阈值 0~255),结果转回 BGR(绕开 ImageHelper 灰度坑)。</summary>
        public static Mat Binarize(Mat src, int threshold)
        {
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            using var bin = new Mat();
            Cv2.Threshold(gray, bin, Math.Clamp(threshold, 0, 255), 255, ThresholdTypes.Binary);
            var result = new Mat();
            Cv2.CvtColor(bin, result, ColorConversionCodes.GRAY2BGR);
            return result;
        }

        /// <summary>灰度化(转回 BGR 以兼容 ImageHelper)。</summary>
        public static Mat ToGray(Mat src)
        {
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            var result = new Mat();
            Cv2.CvtColor(gray, result, ColorConversionCodes.GRAY2BGR);
            return result;
        }

        /// <summary>反色。</summary>
        public static Mat Invert(Mat src)
        {
            var result = new Mat();
            Cv2.BitwiseNot(src, result);
            return result;
        }

        private static bool Similar(Vec3b a, Vec3b b, int t) =>
            Math.Abs(a.Item0 - b.Item0) <= t && Math.Abs(a.Item1 - b.Item1) <= t && Math.Abs(a.Item2 - b.Item2) <= t;
    }
}
