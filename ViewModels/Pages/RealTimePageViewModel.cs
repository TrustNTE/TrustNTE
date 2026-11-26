using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TrustNTE.Models;
using TrustNTE.Services;
using TrustNTE.Services.Interfaces;
using TrustNTE.Services.Triggers;
using TrustNTE.ViewModels;

namespace TrustNTE.ViewModels.Pages
{
    public partial class RealTimePageViewModel : ObservableObject, IDisposable
    {
        // ====== 依赖注入字段 ======
        private readonly IConfigService _configService;
        private readonly IDialogService _dialogService;
        private readonly IWindowService _windowService;
        private readonly AutomationService _automationService;
        private readonly IMapMatchService _mapMatchService;
        private readonly IFileDialogService _fileDialogService;

        public AppConfig Config { get; }
        public string[] ListModes { get; } = { "白名单模式", "黑名单模式" };
        public string[] LootEngines { get; } = { "模板", "OCR" };
        public string[] OptionStrategies { get; } = { "第一个", "第二个" };
        public string[] LogOutputModes { get; } = { "单项输出", "双线输出" };

        private CombatDebugWindowViewModel? _combatDebugVm;

        public int YoloEngineIndex
        {
            get => Config.YoloUseGpu ? 1 : 0; // 0 = CPU, 1 = GPU
            set
            {
                bool newGpu = value == 1;
                if (Config.YoloUseGpu != newGpu)
                {
                    Config.YoloUseGpu = newGpu; // 触发 PropertyChanged → OnConfigChanged → RequestEngine
                }
                OnPropertyChanged();
            }
        }

        // =========        月卡 UI 辅助属性 =========
        public bool IsMonthlyCardCustomTime
        {
            get => !Config.MonthlyCardAllDay;
            set
            {
                Config.MonthlyCardAllDay = !value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTimeSettingVisible));
            }
        }

        public Visibility IsTimeSettingVisible => Config.MonthlyCardAllDay ? Visibility.Collapsed : Visibility.Visible;

        public int MonthlyCardTimeZoneIndex
        {
            get => Config.MonthlyCardUseLocalTime ? 0 : 1;
            set
            {
                Config.MonthlyCardUseLocalTime = (value == 0);
                OnPropertyChanged();
            }
        }

        public RealTimePageViewModel(
            IConfigService configService,
            IDialogService dialogService,
            IWindowService windowService,
            AutomationService automationService,
            IMapMatchService mapMatchService,
            IFileDialogService fileDialogService)
        {
            _configService = configService;
            _dialogService = dialogService;
            _windowService = windowService;
            _automationService = automationService;
            _mapMatchService = mapMatchService;
            _fileDialogService = fileDialogService;

            Config = _configService.Load();
            Config.PropertyChanged += OnConfigChanged;

            //    清洗老用户的旧配置
            if (Config.AutoLootEngine is not ("模板" or "OCR"))
            {
                Config.AutoLootEngine = "模板";
            }

            _automationService.OnRunningStateChanged += OnAutomationRunningStateChanged;

            //    初始化战斗策略列表
            foreach (var n in StrategyService.ListNames()) StrategyNames.Add(n);
            if (StrategyNames.Contains(Config.AutoCombatStrategyName))
                SelectedStrategyName = Config.AutoCombatStrategyName;
        }

        private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
        {
            _configService.Save(Config);

            if (e.PropertyName == nameof(AppConfig.MonthlyCardAllDay))
            {
                OnPropertyChanged(nameof(IsMonthlyCardCustomTime));
                OnPropertyChanged(nameof(IsTimeSettingVisible));
            }

            //    监听推理引擎切换
            if (e.PropertyName == nameof(AppConfig.YoloUseGpu))
            {
                // 通知底层动态重建
                _automationService.YoloProvider?.RequestEngine(Config.YoloUseGpu);
            }

            if (e.PropertyName == nameof(AppConfig.IsCombatDebugWindowOpen))
            {
                if (Config.IsCombatDebugWindowOpen)
                {
                    if (_automationService.CurrentHwnd == IntPtr.Zero)
                    {
                        Config.IsCombatDebugWindowOpen = false;
                        _windowService.ShowStartErrorWindow();
                        return;
                    }

                    if (_combatDebugVm == null)
                    {
                        _combatDebugVm = new CombatDebugWindowViewModel();
                        _combatDebugVm.WindowClosed += () =>
                        {
                            Config.IsCombatDebugWindowOpen = false;
                            _combatDebugVm = null;
                        };
                    }
                    _windowService.ShowCombatDebugWindow(_combatDebugVm);
                }
                else
                {
                    _windowService.CloseCombatDebugWindow();
                    _combatDebugVm = null;
                }
            }
        }

        private void OnAutomationRunningStateChanged(bool isRunning)
        {
            if (!isRunning && Config.IsCombatDebugWindowOpen)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    Config.IsCombatDebugWindowOpen = false;
                });
            }

            if (!isRunning && IsMapMatchingEnabled)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsMapMatchingEnabled = false;
                });
            }
        }

        [RelayCommand]
        private void OpenListConfig()
        {
            var dlg = new TrustNTE.Views.Windows.ListConfigWindow(
                Config.AutoLootListMode,
                Config.AutoLootWhitelistText,
                Config.AutoLootBlacklistText)
            { Owner = System.Windows.Application.Current.MainWindow };

            if (dlg.ShowDialog() == true)
            {
                Config.AutoLootListMode = dlg.FinalListMode;
                Config.AutoLootWhitelistText = dlg.FinalWhiteText;
                Config.AutoLootBlacklistText = dlg.FinalBlackText;
                _configService.Save(Config);
            }
        }

        [RelayCommand]
        private void ShowMonthlyCardDoc()
        {
            TrustNTE.Views.Windows.DocViewerWindow.ShowDoc("月卡功能说明", "monthly_card.md");
        }

        public void Dispose()
        {
            Config.PropertyChanged -= OnConfigChanged;
            _automationService.OnRunningStateChanged -= OnAutomationRunningStateChanged;

            _mapMatchCts?.Cancel();
            _mapMatchCts?.Dispose();

            _windowService.CloseMapMatchDebugWindow();

            if (Config.IsCombatDebugWindowOpen)
            {
                Config.IsCombatDebugWindowOpen = false;
            }
        }
    }
}