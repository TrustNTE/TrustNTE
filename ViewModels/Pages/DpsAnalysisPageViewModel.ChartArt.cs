using System;
using System.Linq;
using System.Windows.Media;

// ── WPF 类型消歧义(Point 被项目全局别名占用,图表内一律写全限定 System.Windows.Point)──
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Geometry = System.Windows.Media.Geometry;

namespace TrustNTE.ViewModels.Pages
{
    /// <summary>
    /// DPS VM · 图表几何部分(partial,与主文件同类型):
    /// 曲线点集 / 圆环扇区 / 属性色板。阶段二数据源替换时本文件不动。
    /// </summary>
    public partial class DpsAnalysisPageViewModel
    {
        private void UpdateLinePoints()
        {
            var line = new System.Windows.Media.PointCollection();
            double hold = 0;
            int n = TimelineBars.Count;
            for (int i = 0; i < n; i++)
            {
                var b = TimelineBars[i];
                double h = b.Kind == "dps" ? b.HeightPct : hold;  // 时停/标记:曲线保持前值
                hold = h;
                line.Add(new System.Windows.Point(i * 10, 200 - h));
            }
            var area = new System.Windows.Media.PointCollection(line);
            if (n > 0) { area.Add(new System.Windows.Point((n - 1) * 10, 200)); area.Add(new System.Windows.Point(0, 200)); }
            LinePoints = line; AreaPoints = area;
        }

        private void UpdateDonut()
        {
            DonutSlices.Clear();
            double sum = Characters.Sum(c => c.Num);
            if (sum <= 0)
            {
                DonutSlices.Add(new DonutSliceVm
                {
                    Geometry = new EllipseGeometry(new System.Windows.Point(110, 110), 100, 100),
                    FillBrush = Freeze("#2A2A3A")
                });
                return;
            }
            const double cx = 110, cy = 110, rO = 100;
            double angle = -90;
            foreach (var c in Characters.OrderByDescending(x => x.Num))
            {
                double sweep = c.Num / sum * 359.999;
                if (sweep < 0.01) continue;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new System.Windows.Point(cx, cy), true, true);
                    g.LineTo(Pt(cx, cy, rO, angle), true, false);
                    g.ArcTo(Pt(cx, cy, rO, angle + sweep), new Size(rO, rO), 0,
                            sweep > 180, SweepDirection.Clockwise, true, false);
                }
                geo.Freeze();
                DonutSlices.Add(new DonutSliceVm { Geometry = geo, FillBrush = AttrBrush(c.Attribute), Label = c.Name });
                angle += sweep;
            }
        }

        private static System.Windows.Point Pt(double cx, double cy, double r, double deg)
        {
            var rad = deg * Math.PI / 180;
            return new System.Windows.Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }

        private static Brush AttrBrush(string? attr) => attr switch
        {
            "相" => Freeze("#FF66B3"),
            "灵" => Freeze("#00F0FF"),
            "光" => Freeze("#FFE600"),
            "暗" => Freeze("#FF4D6D"),
            "咒" => Freeze("#B266FF"),
            "魂" => Freeze("#9EFF6E"),
            _ => Freeze("#667788"),
        };

        private static Brush Freeze(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze(); return b;
        }
    }
}
