using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TrustNTE.Models.Gacha
{
    /// <summary>
    /// 抽卡抓包配置（红线5：端口/去重窗/导出目录均为用户可调，禁止散落魔法数字）。
    /// 注：Phase 2 接入 AppConfig 持久化时需先取 Models\AppConfig.cs 原文，当前先以默认值运行。
    /// 协议指纹常量（marker/信封常量）不属于配置，Phase 3 固化在 Services\Gacha\Protocol\ 层。
    /// </summary>
    public partial class GachaCaptureConfig : ObservableObject
    {
        /// <summary>目标端口列表（逗号分隔）。默认值来自异环已知端口情报。</summary>
        [ObservableProperty] private string _targetPorts = "30031,10012,30230";

        /// <summary>重复包去重窗口（毫秒）。pktmon 多点采集会重复投递，250ms 为原项目验证值。</summary>
        [ObservableProperty] private string _dedupWindowMs = "250";

        /// <summary>导出目录。空 = 默认 UserData\Gacha。</summary>
        [ObservableProperty] private string _exportDirectory = "";


        /// <summary>监听网卡（pcap 设备名）；null/空 = 全部网卡。</summary>
        public string? NicName { get; set; }


        /// <summary>解析端口列表；非法项跳过，全非法回落默认。</summary>
        public IReadOnlyList<int> GetPorts()
        {
            var ports = TargetPorts
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => int.TryParse(p, out var v) ? v : 0)
                .Where(v => v is > 0 and <= 65535)
                .Distinct()
                .ToList();
            return ports.Count > 0 ? ports : new List<int> { 30031, 10012, 30230 };
        }
    }
}
