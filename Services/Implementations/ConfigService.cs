using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Models;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Implementations
{
    public class ConfigService : IConfigService
    {
        private readonly string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        private static readonly object _loadLock = new object();
        private static readonly object _writeLock = new object();
        private static readonly object _updateLock = new object();
        private static AppConfig? _cachedConfig;

        private readonly object _debounceLock = new object();
        private CancellationTokenSource? _saveDebounceCts;

        public AppConfig Load()
        {
            var cached = Volatile.Read(ref _cachedConfig);
            if (cached != null) return cached;

            lock (_loadLock)
            {
                cached = Volatile.Read(ref _cachedConfig);
                if (cached != null) return cached;

                try
                {
                    if (File.Exists(_configPath))
                    {
                        string json = File.ReadAllText(_configPath);
                        var config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                        NormalizeStoryConfig(config);
                        HookAutoSave(config);                       // ← 新增
                        Volatile.Write(ref _cachedConfig, config);
                        return config;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ConfigService] 读取配置失败: {ex.Message}");
                }

                var fallback = new AppConfig();
                HookAutoSave(fallback);                             // ← 新增
                Volatile.Write(ref _cachedConfig, fallback);
                return fallback;
            }
        }

        public void Save(AppConfig config)
        {
            lock (_loadLock)
            {
                Volatile.Write(ref _cachedConfig, config);
            }
            DebouncedSave(config);
        }

        public void Update(Action<AppConfig> mutateAction)
        {
            lock (_updateLock)
            {
                var config = Load();
                mutateAction(config);
                Save(config);
            }
        }

        // ════════════════════════════════════════════════════════════
        // 自动落盘：任何配置属性一改，500ms 防抖写盘。
        // UI 改置信度/脱战时间等 → 即时进缓存（触发器下一帧就吃到）
        //                          → 500ms 后自动写 config.json（重启不丢）
        // ════════════════════════════════════════════════════════════
        private void HookAutoSave(AppConfig config)
        {
            // 1) AppConfig 自身的平铺字段（置信度、脱战时间、各开关……）
            config.PropertyChanged += (_, _) => DebouncedSave(config);

            // 2) 嵌套任务配置对象（独立 ObservableObject，改动不冒泡，逐个挂）
            object?[] nested =
            {
                config.Fishing, config.Music, config.Paw, config.Apartment,
                config.Fountain, config.Market, config.Gift, config.VolleyballHeart,
                config.Hunt, config.Pilgrimage, config.Combat
            };
            foreach (var n in nested)
                if (n is INotifyPropertyChanged inpc)
                    inpc.PropertyChanged += (_, _) => DebouncedSave(config);
        }

        private void DebouncedSave(AppConfig config)
        {
            lock (_debounceLock)
            {
                _saveDebounceCts?.Cancel();
                _saveDebounceCts = new CancellationTokenSource();
                var token = _saveDebounceCts.Token;

                Task.Delay(500, token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    SaveToFile(config);
                }, TaskScheduler.Default);
            }
        }

        private void SaveToFile(AppConfig config)
        {
            lock (_writeLock)
            {
                try
                {
                    string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                    string tempPath = _configPath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, _configPath, true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ConfigService] 保存配置失败: {ex.Message}");
                }
            }
        }

        private static void NormalizeStoryConfig(AppConfig config)
        {
            if (config.AutoDialogueOptionStrategy != "第一个" && config.AutoDialogueOptionStrategy != "第二个")
                config.AutoDialogueOptionStrategy = "第一个";
            if (config.AutoStoryLogMode != "单项输出" && config.AutoStoryLogMode != "双线输出")
                config.AutoStoryLogMode = "单项输出";
        }
    }
}
