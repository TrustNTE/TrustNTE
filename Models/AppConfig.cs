using CommunityToolkit.Mvvm.ComponentModel;
using TrustNTE.Models.TaskConfigs;
//using TrustNTE.ViewModels.Pages.TaskModules;
using System.Collections.Generic;

namespace TrustNTE.Models
{
    public partial class AppConfig : ObservableObject
    {
        // ==========================================
        // 独立任务模块配置
        // 这里的配置项会在玩家点击对应的任务卡片时生效
        // ==========================================
        public AutoFishingConfig Fishing { get; set; } = new();           // 自动钓鱼配置（包含键位、卖鱼买饵等）
        public AutoMusicConfig Music { get; set; } = new();               // 自动音游配置（判定阈值、键位D F J K等）
        public AutoPawConfig Paw { get; set; } = new();                   // 自动粉爪配置
        public AutoApartmentConfig Apartment { get; set; } = new();       // 自动公寓配置（体力、副本挑战等）
        public AutoFountainConfig Fountain { get; set; } = new();         // 喷泉签到配置
        public AutoMarketConfig Market { get; set; } = new();             // 福荫集市配置
        public AutoGiftConfig Gift { get; set; } = new();                 // 羁绊礼物配置（送谁、送几个）
        public AutoVolleyballHeartConfig VolleyballHeart { get; set; } = new(); // 排球之心配置
        public AutoHuntConfig Hunt { get; set; } = new();                 // 异象追猎配置（体力、目标Boss）
        public AutoPilgrimageConfig Pilgrimage { get; set; } = new();     // 异象巡礼配置
        public AutoCombatConfig Combat { get; set; } = new();             // 自动战斗配置（YOLO置信度、是否放大招）
        public AutoRedeemConfig AutoRedeem { get; set; } = new();         // 自动兑换码配置（兑换码列表、坐标等）

        // ==========================================
        // 启动页配置
        // ==========================================
        [ObservableProperty] private string _gamePath = string.Empty;         // 游戏启动器路径
        [ObservableProperty] private string _captureMode = "① PrintWindow";  // 截图引擎模式 (PrintWindow / WGC / DXGI 等)
        [ObservableProperty] private string _triggerInterval = "50";          // 截图间隔毫秒数 (默认50ms=20帧)
        [ObservableProperty] private bool _startGameTogether = true;          // 启动引擎时，是否顺带拉起游戏
        [ObservableProperty] private bool _isBackgroundMode = false;          // 是否开启后台截图（防遮挡）

        [ObservableProperty] private bool _isLogEnabled = true;               // 是否开启日志总开关
        [ObservableProperty] private double _maskOpacity = 0.1;               // 游戏内遮罩背景透明度 (0-1)
        [ObservableProperty] private double _maskTextOpacity = 1.0;           // 游戏内遮罩文字透明度 (0-1)
        [ObservableProperty] private bool _autoClaimRewards = false;          // 是否自动领取奖励（预留功能）
        [ObservableProperty] private bool _enableLocalApi = true;             // 是否开启本地 8888 端口 API

        // ===== 粉爪银行拾取 =====
        [ObservableProperty] private bool _pinkPawPickupEnabled = false;      // 粉爪拾取总开关
        [ObservableProperty] private int _pinkPawFPressCount = 3;             // 识别到粉爪后，按几次F键
        [ObservableProperty] private int _pinkPawFPressIntervalMs = 500;      // 连续按F键的间隔毫秒数
        [ObservableProperty] private string _pinkPawScanRoi = "1122,535,1511,681"; // 粉爪扫描区域 (x1,y1,x2,y2)
        [ObservableProperty] private string _pinkPawDetectMode = "颜色";      // 检测模式（颜色/模板）
        [ObservableProperty] private bool _pinkPawIncludeDialogue = false;    // 是否连对话里的粉爪也要拾取
        [ObservableProperty] private string _mouseDriveType = "通用点击";     // 鼠标驱动方式：物理点击 / 通用点击 / 后台点击

        // ==========================================
        // 快捷键配置
        // ==========================================
        public HotKeyConfig HotKeyConfig { get; set; } = new();               // 旧版快捷键配置（预留）

        // ==========================================
        // 设置页配置
        // ==========================================
        [ObservableProperty] private bool _isAutoStartEnabled = false;        // 是否开启开机自启总开关
        [ObservableProperty] private bool _autoStartEngineOnLogin = false;    // 自启后，是否直接自动开始挂机
        [ObservableProperty] private string _appTheme = "深色";               // 软件主题：深色 / 浅色 / 跟随系统
        [ObservableProperty] private string _uiLanguage = "简体中文";         // 软件UI语言
        [ObservableProperty] private string _gameLanguage = "简体中文";       // 游戏内文本语言（用于OCR识别）

        // ==========================================
        // 自动更新模块配置
        // ==========================================
        [ObservableProperty] private string _notShowNewVersionNoticeEndVersion = string.Empty; // 忽略该版本及以下的更新提示

        // ==========================================
        // 兑换码模块
        // ==========================================
        public string RedeemCodeVersion { get; set; } = string.Empty;         // 上次拉取到的兑换码版本MD5
        public bool RedeemCodeNotificationEnabled { get; set; } = true;       // 是否开启兑换码弹窗提醒

        // ==========================================
        // 辅助操控模块
        // ==========================================
        [ObservableProperty] private bool _autoDriftEnabled = false;          // 自动飘逸开关（长按生效）

        // ==========================================
        // 快捷键配置字典（实际生效的配置）
        // ==========================================
        public Dictionary<string, HotKeyConfigItem> HotKeySettings { get; set; } = new();

        // ==========================================
        // 实时触发模块配置
        // ==========================================
        [ObservableProperty] private bool _autoLootEnabled = true;            // 自动拾取开关
        [ObservableProperty] private string _autoLootEngine = "Paddle";       // 自动拾取使用的OCR引擎
        [ObservableProperty] private string _autoLootKey = "F";               // 拾取交互键
        [ObservableProperty] private string _autoLootListMode = "白名单模式"; // 拾取黑白名单模式
        [ObservableProperty] private string _autoLootWhitelistText = "";      // 白名单列表
        [ObservableProperty] private string _autoLootBlacklistText = "";      // 黑名单列表

        [ObservableProperty] private bool _autoDialogueEnabled = true;        // 自动剧情开关
        [ObservableProperty] private bool _autoTeleportEnabled = true;        // 自动传送开关（仅影响剧情里的传送按钮点击）
        [ObservableProperty] private bool _autoDialogueAutoOption = true;     // 自动选择对话选项开关
        [ObservableProperty] private string _autoDialogueOptionStrategy = "第一个"; // 选项策略：第一个 / 第二个
        [ObservableProperty] private string _autoStoryLogMode = "双线输出";   // 剧情日志模式：单项 / 双线

        [ObservableProperty] private bool _dailyRewardEnabled = true;         // 自动领取每日奖励开关
        [ObservableProperty] private bool _yikacheEnabled = true;             // 一咖舍开关
        [ObservableProperty] private bool _autoRestoreFocus = true;           // 物理点击后，是否自动归还鼠标焦点
        [ObservableProperty] private bool _autoCombatEnabled = false;         // 自动战斗开关（YOLO）

        // ===== 自动战斗细分配置 =====
        [ObservableProperty] private bool _autoCombatUseUltimate = true;      // 是否自动放大招 (Q)
        [ObservableProperty] private bool _autoCombatTargeting = true;        // 是否自动锁定目标
        [ObservableProperty] private double _autoCombatConfidence = 0.25;     // YOLO识别置信度阈值 (越低越容易识别，但误判高)
        [ObservableProperty] private bool _isCombatDebugWindowOpen = false;   // 战斗实时调试窗口是否打开

          // ===== 剧情提示开关 =====
        [ObservableProperty] private bool _autoPlotDoNotPromptEnabled = true; // 自动点击「今日不再提示」
          // ===== 自动领取「环期赠礼签到」开关 =====
        [ObservableProperty] private bool _autoCheckInClaimEnabled = true;    // 自动点击「环期赠礼签到可领取」

        // ===== 月卡/环石自动领取 =====
        [ObservableProperty] private bool _monthlyCardEnabled = false;        // 月卡自动领取总开关
        [ObservableProperty] private bool _monthlyCardAllDay = true;          // true=全天随时领，false=指定时间段领
        [ObservableProperty] private string _monthlyCardStartTime = "03:50";  // 指定时间段的开始时间
        [ObservableProperty] private string _monthlyCardEndTime = "04:10";    // 指定时间段的结束时间
        [ObservableProperty] private bool _monthlyCardUseLocalTime = true;    // true=读取本地时间，false=强制读取北京时间
        public string LastMonthlyCardClaimDate { get; set; } = string.Empty;  // 记录上次领取的日期，用于跨天防重

        // ==========================================
        // 游戏守护（Supervisor）配置
        // ==========================================
        [ObservableProperty] private bool _autoUpdateGame = true;             // 启动器检测到更新时，是否自动更新游戏
        [ObservableProperty] private bool _autoReconnectEnabled = true;       // 掉线时，是否自动点击重连
        [ObservableProperty] private bool _autoRestartOnCrash = true;         // 游戏闪退时，是否自动重启游戏
        [ObservableProperty] private bool _autoResumeTask = true;             // 重连/重启后，是否自动恢复之前未完成的任务
        [ObservableProperty] private bool _idleGuardEnabled = true;           // 空闲守护：引擎未启动时，是否监控掉线和进入游戏

        // ==========================================
        // 遮罩位置配置
        // ==========================================
        // (0=左下 1=左上 2=右下 3=右上)
        [ObservableProperty] private int _maskPosition = 0;

        // ==========================================
        // 日志自定义颜色 (Hex格式)
        // ==========================================
        [ObservableProperty] private string _logColorDefault = "#00FFFF";     // 默认文本颜色（青色）
        [ObservableProperty] private string _logColorError = "#FF5252";       // 错误日志颜色（红色）
        [ObservableProperty] private string _logColorGuard = "#FFA940";       // 守护/监管日志颜色（橙色）
        [ObservableProperty] private string _logColorHighFreq = "#7A828E";    // 高频日志颜色（灰色，防瞎眼）
        [ObservableProperty] private string _logColorMouse = "#FF00FF";       // 鼠标操作日志颜色（洋红）
        [ObservableProperty] private string _logColorKeyboard = "#00FF00";    // 键盘操作日志颜色（亮绿）
    }
}