using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TrustNTE.Models.Toolkit
{
    /// <summary>取色器单点状态(纯数据,列表整体替换)。</summary>
    public sealed class DevToolsPickState
    {
        public bool Enabled { get; set; }
        public string Logical { get; set; } = "";
        public string RgbHex { get; set; } = "";
        public string Bias { get; set; } = "000000";
    }

    /// <summary>
    /// 开发者工具(取色器+截图器)配置,挂在 AppConfig.DevTools 下。
    /// ConfigService.HookAutoSave 已挂钩本对象:标量属性一改 500ms 自动落盘 config.json。
    /// </summary>
    public partial class DevToolsConfig : ObservableObject
    {
        // —— 取色器 ——
        [ObservableProperty] private int _previewIntervalMs = 500;
        public List<DevToolsPickState> Picks { get; set; } = new();

        // —— 截图器 ——
        [ObservableProperty] private int _fillTolerance = 10;
        [ObservableProperty] private int _cropTolerance = 10;
        [ObservableProperty] private int _binThreshold = 128;
        public List<string> EditorColors { get; set; } = new();
    }

    /// <summary>
    /// 旧独立状态文件(UserData\DevToolsState.json)→ config.json 一次性迁移。
    /// 幂等:仅当目标 Picks/EditorColors 全空才灌入;旧文件保留不删。
    /// </summary>
    public static class DevToolsLegacyMigrator
    {
        private static readonly string LegacyPath =
            Path.Combine(AppContext.BaseDirectory, "UserData", "DevToolsState.json");

        private sealed class LegacyPick
        { public bool Enabled { get; set; } public string Logical { get; set; } = ""; public string RgbHex { get; set; } = ""; public string Bias { get; set; } = "000000"; }

        private sealed class LegacyState
        {
            public int PreviewIntervalMs { get; set; } = 500;
            public List<LegacyPick> Picks { get; set; } = new();
            public List<string> EditorColors { get; set; } = new();
            public int FillTolerance { get; set; } = 10;
            public int CropTolerance { get; set; } = 10;
            public int BinThreshold { get; set; } = 128;
        }

        public static bool TryMigrate(DevToolsConfig target)
        {
            try
            {
                if (target.Picks.Count > 0 || target.EditorColors.Count > 0) return false; // 已有数据,不覆盖
                if (!File.Exists(LegacyPath)) return false;
                var json = File.ReadAllText(LegacyPath);
                if (string.IsNullOrWhiteSpace(json)) return false;
                var old = JsonSerializer.Deserialize<LegacyState>(json);
                if (old == null) return false;

                target.PreviewIntervalMs = Math.Clamp(old.PreviewIntervalMs, 50, 2000);
                target.FillTolerance = Math.Clamp(old.FillTolerance, 0, 255);
                target.CropTolerance = Math.Clamp(old.CropTolerance, 0, 255);
                target.BinThreshold = Math.Clamp(old.BinThreshold, 0, 255);
                target.Picks = old.Picks
                    .Select(p => new DevToolsPickState { Enabled = p.Enabled, Logical = p.Logical, RgbHex = p.RgbHex, Bias = p.Bias })
                    .ToList();
                target.EditorColors = old.EditorColors.Take(50).ToList();
                return true;
            }
            catch { return false; }
        }
    }
}
