using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TrustNTE.Tests.Tools.ActivityProgress
{
    /// <summary>单个进度条段的 ROI 配置项</summary>
    public class RoiItem
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }

        /// <summary>该段填满后对应的档位（20/40/60/80/100）</summary>
        public int Milestone { get; set; }
    }

    /// <summary>活跃度检测的配置文件（序列化到 activity_roi.json）</summary>
    public class ActivityConfig
    {
        /// <summary>5 段进度条 ROI（1080P 归一化坐标）</summary>
        public List<RoiItem> Rois { get; set; } = new()
        {
            new RoiItem { X = 493,  Y = 233, W = 153, H = 10, Milestone = 20  },
            new RoiItem { X = 723,  Y = 233, W = 163, H = 9,  Milestone = 40  },
            new RoiItem { X = 962,  Y = 234, W = 163, H = 9,  Milestone = 60  },
            new RoiItem { X = 1201, Y = 234, W = 165, H = 8,  Milestone = 80  },
            new RoiItem { X = 1440, Y = 234, W = 165, H = 8,  Milestone = 100 },
        };

        // ── #4DFAD9 青色 HSV 阈值（进度条填充色）──
        public byte HMin { get; set; } = 80;
        public byte HMax { get; set; } = 95;
        public byte SMin { get; set; } = 120;
        public byte VMin { get; set; } = 150;

        /// <summary>一列中至少有 N 个青色像素才算"该列已填充"（抗噪，2 足够）</summary>
        public int MinColHits { get; set; } = 2;

        /// <summary>已填充列占比 ≥ 该值 → 该段判满（档位达成）</summary>
        public double FullRatio { get; set; } = 0.95;

        /// <summary>连续命中 N 帧才确认，防闪烁</summary>
        public int ConfirmFrames { get; set; } = 2;

        private const string FilePath = "activity_roi.json";

        public static ActivityConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<ActivityConfig>(json) ?? new ActivityConfig();
                }
            }
            catch { }
            return new ActivityConfig();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }
}
