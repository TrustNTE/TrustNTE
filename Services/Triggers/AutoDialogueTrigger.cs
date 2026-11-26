using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TrustNTE.Helpers;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Triggers
{
    public class AutoDialogueTrigger : ITaskTrigger
    {
        private enum StoryActionType
        {
            Click,
            KeyPress,
            DialogueChoice
        }

        private sealed class StoryRule
        {
            public string DisplayName { get; init; } = string.Empty;
            public string TemplateGroup { get; init; } = string.Empty;
            public Rect SearchRoi { get; init; }
            public string? SecondTemplateGroup { get; init; }
            public Rect? SecondSearchRoi { get; init; }
            public StoryActionType Action { get; init; }
            public int VirtualKey { get; init; }
            public int? ChoiceClickX { get; init; }
            public int? ChoiceClickY { get; init; }

            /// <summary>是否需要开启了“自动传送”才执行</summary>
            public bool RequireTeleport { get; init; } = false;

            /// <summary>【新增】需要位于活动界面（Nte_Activity 命中）才参与模板匹配</summary>
            public bool RequireActivityUi { get; init; } = false;

            /// <summary>【新增】本规则动作执行后的冷却毫秒；默认全局 500，环期赠礼用 1000 实现“延迟1秒”</summary>
            public int PostActionCooldownMs { get; init; } = 500;

            /// <summary>自定义日志前缀，若为空则使用默认拼接</summary>
            public string? CustomLogTag { get; init; }

            /// <summary>对应的配置开关名，若为空则不检查</summary>
            public string? ConfigToggleProperty { get; init; }
        }


        private static readonly StoryRule[] StoryRules =
        {
            // ── 剧情提示（无门控，保持原样） ──
            new StoryRule { DisplayName = "剧情提示:今日不在提示", TemplateGroup = "Nte_PlotDoNotPromptToday", SearchRoi = new Rect(883, 534, 65, 46), Action = StoryActionType.Click, CustomLogTag = "剧情提示:今日不在提示", ConfigToggleProperty = "AutoPlotDoNotPromptEnabled" },

            // ══ 签到模块（全部挂门控：Nte_Activity 命中 + AutoCheckInClaimEnabled 勾选才匹配）══
            // ── 区域1-6：普通“可领取” ──
            new StoryRule { DisplayName = "环期赠礼→签到领取奖励", TemplateGroup = "Nte_CheckInClaimable", SearchRoi = new Rect(1109, 620, 79, 39), Action = StoryActionType.Click, CustomLogTag = "环期赠礼→签到领取奖励", ConfigToggleProperty = "AutoCheckInClaimEnabled", RequireActivityUi = true },
            new StoryRule { DisplayName = "环期赠礼→签到领取奖励", TemplateGroup = "Nte_CheckInClaimable", SearchRoi = new Rect(1291, 621, 70, 42), Action = StoryActionType.Click, CustomLogTag = "环期赠礼→签到领取奖励", ConfigToggleProperty = "AutoCheckInClaimEnabled", RequireActivityUi = true },
            new StoryRule { DisplayName = "环期赠礼→签到领取奖励", TemplateGroup = "Nte_CheckInClaimable", SearchRoi = new Rect(1459, 623, 82, 38), Action = StoryActionType.Click, CustomLogTag = "环期赠礼→签到领取奖励", ConfigToggleProperty = "AutoCheckInClaimEnabled", RequireActivityUi = true },
            new StoryRule { DisplayName = "环期赠礼→签到领取奖励", TemplateGroup = "Nte_CheckInClaimable", SearchRoi = new Rect(1111, 902, 77, 41), Action = StoryActionType.Click, CustomLogTag = "环期赠礼→签到领取奖励", ConfigToggleProperty = "AutoCheckInClaimEnabled", RequireActivityUi = true },
            new StoryRule { DisplayName = "环期赠礼→签到领取奖励", TemplateGroup = "Nte_CheckInClaimable", SearchRoi = new Rect(1287, 899, 78, 41), Action = StoryActionType.Click, CustomLogTag = "环期赠礼→签到领取奖励", ConfigToggleProperty = "AutoCheckInClaimEnabled", RequireActivityUi = true },
            new StoryRule { DisplayName = "环期赠礼→签到领取奖励", TemplateGroup = "Nte_CheckInClaimable", SearchRoi = new Rect(1466, 900, 77, 42), Action = StoryActionType.Click, CustomLogTag = "环期赠礼→签到领取奖励", ConfigToggleProperty = "AutoCheckInClaimEnabled", RequireActivityUi = true },

            // ── 区域7：第七天签到，专用模板（不再用 Nte_CheckInClaimable） ──
            new StoryRule { DisplayName = "环期赠礼→签到领取奖励", TemplateGroup = "Nte_SeventhDayCheckIn", SearchRoi = new Rect(1674, 874, 119, 58), Action = StoryActionType.Click, CustomLogTag = "环期赠礼→签到领取奖励", ConfigToggleProperty = "AutoCheckInClaimEnabled", RequireActivityUi = true },

            // ── 环期赠礼入口：必须放在签到规则之后（有可领签到时轮不到它） ──
            // 若 MatchAny 是前缀展开，“Nte_CycleGift”自动覆盖 CycleGift1/2；
            // 若是精确匹配，请拆成 TemplateGroup="Nte_CycleGift1" 和 "Nte_CycleGift2" 两条
            new StoryRule { DisplayName = "点击环期赠礼", TemplateGroup = "Nte_CycleGift", SearchRoi = new Rect(41, 229, 86, 83), Action = StoryActionType.Click, CustomLogTag = "点击环期赠礼", ConfigToggleProperty = "AutoCheckInClaimEnabled", RequireActivityUi = true, PostActionCooldownMs = 1000 },

            // ── 传送模块（受界面开关控制） ──
            new StoryRule { DisplayName = "传送", TemplateGroup = "Nte_Teleport", SearchRoi = new Rect(1488, 940, 68, 44), Action = StoryActionType.Click, RequireTeleport = true },
            new StoryRule { DisplayName = "传送", TemplateGroup = "Nte_Teleport", SearchRoi = new Rect(1615, 937, 66, 45), Action = StoryActionType.Click, RequireTeleport = true },

            // ── 运通全城完成派送 ──
            new StoryRule { DisplayName = "运通全城完成派送", TemplateGroup = "Nte_YunTongCitywideDeliveryCompleted", SearchRoi = new Rect(1153, 873, 63, 43), Action = StoryActionType.Click },

            // ── 退出消息列表（End + Message 双模板 AND） ──
            new StoryRule
            {
                DisplayName = "退出消息列表",
                TemplateGroup = "Nte_DialogueEnd",
                SearchRoi = new Rect(1222, 871, 71, 40),
                SecondTemplateGroup = "Nte_DialogueMessage",
                SecondSearchRoi = new Rect(108, 32, 76, 53),
                Action = StoryActionType.KeyPress,
                VirtualKey = KeyCode.Esc
            },

            // ── 原有的其他规则 ──
            new StoryRule { DisplayName = "确认跳过", TemplateGroup = "Nte_ConfirmSkip", SearchRoi = new Rect(1151, 614, 189, 110), Action = StoryActionType.Click },
            new StoryRule { DisplayName = "概述确认", TemplateGroup = "Nte_OverviewConfirm", SearchRoi = new Rect(1115, 622, 227, 126), Action = StoryActionType.Click },
            new StoryRule { DisplayName = "剧情单独跳过", TemplateGroup = "Nte_StorySkip", SearchRoi = new Rect(1814, 26, 84, 79), Action = StoryActionType.KeyPress, VirtualKey = KeyCode.Esc },
            new StoryRule { DisplayName = "剧情加感叹号加跳过", TemplateGroup = "Nte_StorySkipExclamation", SearchRoi = new Rect(1676, 25, 223, 80), Action = StoryActionType.KeyPress, VirtualKey = KeyCode.Esc },
            new StoryRule { DisplayName = "按下V调整时间", TemplateGroup = "Nte_PressVAdjustTime", SearchRoi = new Rect(76, 285, 214, 85), Action = StoryActionType.KeyPress, VirtualKey = KeyCode.V },
            new StoryRule { DisplayName = "调整时间确认", TemplateGroup = "Nte_AdjustTimeConfirm", SearchRoi = new Rect(1061, 648, 202, 125), Action = StoryActionType.Click },
            //new StoryRule { DisplayName = "消息交互", TemplateGroup = "Nte_MessageInteract", SearchRoi = new Rect(899, 631, 334, 256), Action = StoryActionType.Click },
            new StoryRule { DisplayName = "前往任务", TemplateGroup = "Nte_GoTask", SearchRoi = new Rect(1387, 732, 198, 131), Action = StoryActionType.Click },
            new StoryRule { DisplayName = "追踪任务", TemplateGroup = "Nte_TrackTask", SearchRoi = new Rect(1521, 963, 217, 84), Action = StoryActionType.Click },
            new StoryRule { DisplayName = "按下V追踪任务", TemplateGroup = "Nte_PressVTrackTask", SearchRoi = new Rect(77, 288, 165, 95), Action = StoryActionType.KeyPress, VirtualKey = KeyCode.V },
            new StoryRule { DisplayName = "取消追踪", TemplateGroup = "Nte_CancelTrackTask", SearchRoi = new Rect(1549, 967, 168, 66), Action = StoryActionType.KeyPress, VirtualKey = KeyCode.Esc },
            //new StoryRule { DisplayName = "领取奖励", TemplateGroup = "Nte_GetReward", SearchRoi = new Rect(1566, 260, 232, 609), Action = StoryActionType.Click },
            new StoryRule { DisplayName = "点击空白处", TemplateGroup = "Nte_ClickBlank", SearchRoi = new Rect(837, 935, 279, 54), Action = StoryActionType.KeyPress, VirtualKey = KeyCode.Esc },
            new StoryRule { DisplayName = "副本领取", TemplateGroup = "Nte_DungeonClaim", SearchRoi = new Rect(1156, 554, 166, 76), Action = StoryActionType.KeyPress, VirtualKey = KeyCode.F },
            new StoryRule { DisplayName = "棋盘跳过", TemplateGroup = "Nte_SkipChessboard", SearchRoi = new Rect(1822, 21, 74, 71), Action = StoryActionType.KeyPress, VirtualKey = KeyCode.Esc },
            new StoryRule { DisplayName = "抽卡点击", TemplateGroup = "Nte_CloseChessboard", SearchRoi = new Rect(859, 1011, 213, 39), Action = StoryActionType.KeyPress, VirtualKey = KeyCode.Esc },
            new StoryRule { DisplayName = "下一页", TemplateGroup = "Nte_NextPage", SearchRoi = new Rect(1521, 759, 153, 79), Action = StoryActionType.Click },
            new StoryRule { DisplayName = "领取礼物奖励", TemplateGroup = "Nte_GiftClaim", SearchRoi = new Rect(1171, 811, 311, 83), Action = StoryActionType.Click },
            //new StoryRule { DisplayName = "角色礼物领取", TemplateGroup = "Nte_CharGiftClaim", SearchRoi = new Rect(1636, 172, 121, 818), Action = StoryActionType.Click },
            new StoryRule { DisplayName = "角色礼物领取新增", TemplateGroup = "Nte_CloseGiftClaim", SearchRoi = new Rect(822, 1009, 303, 53), Action = StoryActionType.Click },
            new StoryRule { DisplayName = "派送完成哈嗦", TemplateGroup = "Nte_HathorDelivery", SearchRoi = new Rect(957, 675, 366, 251), Action = StoryActionType.Click },
            new StoryRule { DisplayName = "派送完成", TemplateGroup = "Nte_DeliveryComplete", SearchRoi = new Rect(957, 675, 366, 251), Action = StoryActionType.Click },
           // new StoryRule { DisplayName = "幽灵号点击空白关闭", TemplateGroup = "Nte_GhostClickBlankClose", SearchRoi = new Rect(837, 662, 239, 387), Action = StoryActionType.Click },

            new StoryRule
            {
                DisplayName = "对话选项",
                TemplateGroup = "Nte_DialogEllipsis",
                SearchRoi = new Rect(1747, 33, 51, 59),
                SecondTemplateGroup = "Nte_Forbid",
                SecondSearchRoi = new Rect(1826, 45, 39, 41),
                Action = StoryActionType.DialogueChoice,
                VirtualKey = KeyCode.F,
                ChoiceClickX = 1346,
                ChoiceClickY = 807
            },
        };

        // ── 门控：活动界面探测 ROI（由你给的 109,39,183,84 即 x1,y1,x2,y2 换算） ──
        private static readonly Rect ActivityUiRoi = new Rect(109, 39, 74, 45);

        // 预计算：哪些开关参与门控（本帧只需检查这些开关，命中才做一次 Activity 匹配）
        private static readonly string[] ActivityGateToggles = StoryRules
            .Where(r => r.RequireActivityUi && !string.IsNullOrEmpty(r.ConfigToggleProperty))
            .Select(r => r.ConfigToggleProperty!)
            .Distinct()
            .ToArray();

        // 反射缓存：属性名 → PropertyInfo（避免每帧每规则反射查找）
        private static readonly Dictionary<string, PropertyInfo?> TogglePropertyCache = new();
        private static readonly HashSet<string> MissingToggleWarned = new();

        public string Name => "自动剧情";
        public bool IsEnabled { get; set; } = false;

        private readonly IConfigService _configService;
        private readonly IInputRouter _router;
        private readonly ILogService _logService;
        private readonly AutoStoryTemplateService _storyTemplates;
        private readonly IntPtr _hwnd;

        // 冷却  “截止时刻”（TickCount64 毫秒）：过期后单字段自然失效，无需清旧时长。
        // 用 Environment.TickCount64 避免 DateTime.Now 受 NTP 校时跳变影响（500~1000ms 短间隔更稳）。
        private long _nextActionAllowedTick = 0;

        // 显式等待：环期赠礼点击后进入“等模板消失”状态。
        // 模板还在 → 本帧拒绝一切匹配（绝不重复点）；消失/超时 → 立即恢复。
        private (string Template, Rect Roi, long DeadlineTick)? _awaitingTransition;

        private bool _lastActivityUiState = false;
        private Rect? _lastHitRect;
        private string _lastHitGroup = string.Empty;
        private DateTime _lastHitTime = DateTime.MinValue;

        public AutoDialogueTrigger(
            IConfigService configService,
            IInputRouter router,
            ILogService logService,
            AutoStoryTemplateService storyTemplates,
            IntPtr hwnd)
        {
            _configService = configService;
            _router = router;
            _logService = logService;
            _storyTemplates = storyTemplates;
            _hwnd = hwnd;
        }

        public void DrawDebug(Mat drawMat)
        {
            if (!IsEnabled) return;
            // 门控探测框（洋红）——调试时肉眼确认 Activity 是否命中
            Cv2.Rectangle(drawMat, ActivityUiRoi, new Scalar(255, 0, 255), 1);
            foreach (var rule in StoryRules)
            {
                Cv2.Rectangle(drawMat, rule.SearchRoi, new Scalar(0, 255, 255), 1);
                if (rule.SecondSearchRoi.HasValue)
                    Cv2.Rectangle(drawMat, rule.SecondSearchRoi.Value, new Scalar(255, 0, 255), 1);
            }
            if (_lastHitRect.HasValue && (DateTime.Now - _lastHitTime).TotalSeconds <= 1)
            {
                Cv2.Rectangle(drawMat, _lastHitRect.Value, new Scalar(0, 255, 0), 2);
                Cv2.PutText(drawMat, _lastHitGroup,
                    new Point(_lastHitRect.Value.X, Math.Max(12, _lastHitRect.Value.Y - 5)),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 0), 1);
            }
        }

        public void OnCapture(Mat resizedMat, double scale)
        {
            var config = _configService.Load();
            if (!config.AutoDialogueEnabled) return;

            // 闸门：未到期直接返回，线程继续干活，只是不匹配不点击
            if (Environment.TickCount64 < _nextActionAllowedTick) return;

            // 显式等待：环期赠礼点击后，模板还在 → 本帧不匹配任何规则；消失/超时 → 立即恢复
            if (_awaitingTransition.HasValue)
            {
                var (tpl, roi, deadline) = _awaitingTransition.Value;
                bool stillThere = _storyTemplates.MatchAny(
                    resizedMat, roi, tpl, AutoStoryTemplateService.MatchThreshold) != null;

                if (!stillThere || Environment.TickCount64 >= deadline)
                    _awaitingTransition = null;   // 页面切走了 → 立即恢复；超时 → 兜底恢复
                else
                    return;                       // 还没切走 → 本帧不匹配任何规则
            }

            // ══ 门控探测：本帧最多匹配一次 Nte_Activity，结果缓存给所有门控规则复用 ══
            // 开关全关时连这一次匹配都不做，真正做到“不勾选就不做模版匹配”
            bool activityUi = false;
            if (ActivityGateToggles.Any(t => IsToggleEnabled(config, t)))
            {
                activityUi = _storyTemplates.MatchAny(
                    resizedMat, ActivityUiRoi, "Nte_Activity", AutoStoryTemplateService.MatchThreshold) != null;

                // 状态沿日志：进入活动界面时只提示一次，避免刷屏
                if (activityUi && !_lastActivityUiState)
                    _logService.LogInfo("[自动剧情] 当前位于活动界面");
            }
            _lastActivityUiState = activityUi;

            foreach (var rule in StoryRules)
            {
                // 1. 检查传送开关
                if (rule.RequireTeleport && !config.AutoTeleportEnabled) continue;

                // 2. 【新增】不在活动界面 → 跳过，把匹配预算留给其他规则
                if (rule.RequireActivityUi && !activityUi) continue;

                // 3. 检查独立配置开关
                if (!IsToggleEnabled(config, rule.ConfigToggleProperty)) continue;

                Rect? hitRect = _storyTemplates.MatchAny(
                    resizedMat, rule.SearchRoi, rule.TemplateGroup, AutoStoryTemplateService.MatchThreshold);
                if (hitRect == null) continue;

                // 复合规则双模板 AND
                if (rule.SecondTemplateGroup != null && rule.SecondSearchRoi.HasValue)
                {
                    Rect? secondHit = _storyTemplates.MatchAny(
                        resizedMat, rule.SecondSearchRoi.Value, rule.SecondTemplateGroup, AutoStoryTemplateService.MatchThreshold);
                    if (secondHit == null) continue;
                }

                ExecuteRule(rule, hitRect.Value, config);

                // 环期赠礼：  “等模板消失”，取代原来的固定 1 秒冷却。
                // 模板还在就绝不重复点（无论切换实际花多久）；切走立即恢复；3 秒兜底防点击没生效卡死。
                if (rule.TemplateGroup == "Nte_CycleGift")
                {
                    _awaitingTransition = (rule.TemplateGroup, rule.SearchRoi, Environment.TickCount64 + 3000);
                }
                else
                {
                    _nextActionAllowedTick = Environment.TickCount64 + rule.PostActionCooldownMs;
                }

                _lastHitRect = hitRect.Value;
                _lastHitGroup = rule.TemplateGroup;
                _lastHitTime = DateTime.Now;
                return; // 一次只执行一个规则
            }
        }

        /// <summary>带缓存 + 防呆的开关读取。属性缺失时一次性警告并保持旧行为（视为开启）</summary>
        private bool IsToggleEnabled(Models.AppConfig config, string? propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return true;

            if (!TogglePropertyCache.TryGetValue(propertyName, out var prop))
            {
                prop = typeof(Models.AppConfig).GetProperty(propertyName);
                TogglePropertyCache[propertyName] = prop;
            }

            if (prop == null)
            {
                if (MissingToggleWarned.Add(propertyName))
                    _logService.LogInfo($"[自动剧情] [警告] 配置项 {propertyName} 不存在，开关检查被跳过（视为开启），请检查属性名拼写");
                return true;
            }

            return prop.GetValue(config) is bool enabled && enabled;
        }

        private void ExecuteRule(StoryRule rule, Rect hitRect, Models.AppConfig config)
        {
            string head = BuildRoiHead(rule);
            string action;

            switch (rule.Action)
            {
                case StoryActionType.Click:
                    {
                        int centerX = hitRect.X + hitRect.Width / 2;
                        int centerY = hitRect.Y + hitRect.Height / 2;
                        var (px, py) = _router.ClickLogical(_hwnd, centerX, centerY);
                        action = _router.ClickTag(px, py, "左键点击");
                        break;
                    }
                case StoryActionType.KeyPress:
                    {
                        _router.KeyPress(_hwnd, rule.VirtualKey, 50);
                        action = _router.KeyTag(rule.VirtualKey);
                        break;
                    }
                case StoryActionType.DialogueChoice:
                default:
                    {
                        string strategy = config.AutoDialogueOptionStrategy;
                        if (strategy == "第二个" && rule.ChoiceClickX.HasValue && rule.ChoiceClickY.HasValue)
                        {
                            var (px, py) = _router.ClickLogical(_hwnd, rule.ChoiceClickX.Value, rule.ChoiceClickY.Value);
                            action = _router.ClickTag(px, py, "左键点击") + "（策略:第二个）";
                        }
                        else
                        {
                            _router.KeyPress(_hwnd, rule.VirtualKey, 50);
                            action = _router.KeyTag(rule.VirtualKey) + $"（策略:{strategy}）";
                        }
                        break;
                    }
            }

            EmitStoryLog(rule, head, action, config.AutoStoryLogMode);
        }

        private static string BuildRoiHead(StoryRule rule)
        {
            if (!string.IsNullOrEmpty(rule.CustomLogTag)) return rule.CustomLogTag;

            if (rule.SecondTemplateGroup == null || !rule.SecondSearchRoi.HasValue)
            {
                return $"[自动剧情] {rule.DisplayName} {rule.TemplateGroup} 判断区域 " +
                       $"{rule.SearchRoi.X},{rule.SearchRoi.Y},{rule.SearchRoi.Right},{rule.SearchRoi.Bottom}," +
                       $"宽高({rule.SearchRoi.Width},{rule.SearchRoi.Height})";
            }

            var b = rule.SecondSearchRoi.Value;
            return $"[自动剧情] {rule.DisplayName} {rule.TemplateGroup}+{rule.SecondTemplateGroup} 判断区域A " +
                   $"{rule.SearchRoi.X},{rule.SearchRoi.Y},{rule.SearchRoi.Right},{rule.SearchRoi.Bottom}," +
                   $"宽高({rule.SearchRoi.Width},{rule.SearchRoi.Height}) 判断区域B " +
                   $"{b.X},{b.Y},{b.Right},{b.Bottom},宽高({b.Width},{b.Height})";
        }

        private void EmitStoryLog(StoryRule rule, string head, string action, string logMode)
        {
            if (!string.IsNullOrEmpty(rule.CustomLogTag))
            {
                _logService.LogInfo($"[自动剧情] {rule.CustomLogTag} {action}");
                return;
            }

            if (logMode == "双线输出")
            {
                _logService.LogInfo(head);
                _logService.LogInfo(action);
            }
            else
            {
                _logService.LogInfo($"{head} {action}");
            }
        }
    }
}