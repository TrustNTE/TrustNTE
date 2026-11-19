using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrustNTE.Services.Gacha.Maps
{
    public static class GachaMapCatalog
    {
        private const string CoreSchema = "nte-map-core";
        private const long CoreVersion = 1;
        private const string LocaleSchema = "nte-map-locale";
        private const long LocaleVersion = 1;

        private static readonly object _gate = new();
        private static bool _loaded;
        private static bool _available;
        private static string? _loadError;

        private static Dictionary<string, MappedItem> _items = new();
        private static Dictionary<string, string> _itemsByFolded = new();
        private static Dictionary<string, string> _aliases = new();
        private static Dictionary<string, MappedPool> _pools = new();
        private static Dictionary<string, MappedBanner> _banners = new();

        // ── 头像表（AvatarDataTable.json，独立于 core.json，加载失败互不影响） ──
        private static bool _avatarLoaded;
        private static Dictionary<string, string> _avatarByItemId = new();   // 纯数字 Row key → 引用文件名
        private static string[]? _avatarDirs;                                // 256 / 200 / 100 优先序
                                                                             // ── [弧盘兜底] DT_ForkItemData.json：覆盖 core.json 弧盘（12 项）──
        private static bool _forkTableLoaded;
        private static Dictionary<string, string> _forkIconByItemId = new(StringComparer.Ordinal);
        private static Dictionary<string, string> _forkNameByItemId = new(StringComparer.Ordinal);
        private static Dictionary<string, int> _forkRarityByItemId = new(StringComparer.Ordinal);


        public static bool IsAvailable { get { EnsureLoaded(); return _available; } }
        public static string? LoadError { get { EnsureLoaded(); return _loadError; } }

        // ── 组装后模型 ──
        private sealed record MappedItem(string Name, int Rarity, string? Category, string? Subtype, Dictionary<string, string>? AssetRefs);
        private sealed record MappedPool(string Name, string? GroupLabel, string? Title);
        private sealed record MappedBanner(string PoolId, string PoolKind, string BannerType, string Title);

        private sealed class MapCoreDocument
        {
            [JsonPropertyName("schema")] public string Schema { get; set; } = "";
            [JsonPropertyName("schema_version")] public long SchemaVersion { get; set; }
            [JsonPropertyName("items")] public Dictionary<string, MapItemCore> Items { get; set; } = new();
            [JsonPropertyName("item_aliases")] public Dictionary<string, string>? ItemAliases { get; set; }
            [JsonPropertyName("pools")] public Dictionary<string, MapPoolCore> Pools { get; set; } = new();
            [JsonPropertyName("banner_sources")] public Dictionary<string, MapBannerSourceCore> BannerSources { get; set; } = new();
        }

        private sealed class MapItemCore
        {
            [JsonPropertyName("rarity")] public int Rarity { get; set; }
            [JsonPropertyName("category")] public string? Category { get; set; }
            [JsonPropertyName("subtype")] public string? Subtype { get; set; }
            [JsonPropertyName("color")] public string? Color { get; set; }
            [JsonPropertyName("asset_refs")] public Dictionary<string, string>? AssetRefs { get; set; }
        }

        private sealed class MapPoolCore
        {
            [JsonPropertyName("pickup_item_ids")] public List<string>? PickupItemIds { get; set; }
        }

        private sealed class MapBannerSourceCore
        {
            [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";
            [JsonPropertyName("pool_id")] public string PoolId { get; set; } = "";
            [JsonPropertyName("pool_kind")] public string PoolKind { get; set; } = "";
            [JsonPropertyName("banner_type")] public string BannerType { get; set; } = "";
            [JsonPropertyName("rate_up_5")] public List<string>? RateUp5 { get; set; }
            [JsonPropertyName("rate_up_4")] public List<string>? RateUp4 { get; set; }
            [JsonPropertyName("rule_id")] public string RuleId { get; set; } = "";
        }

        private sealed class MapLocaleDocument
        {
            [JsonPropertyName("schema")] public string Schema { get; set; } = "";
            [JsonPropertyName("schema_version")] public long SchemaVersion { get; set; }
            [JsonPropertyName("locale")] public string Locale { get; set; } = "";
            [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
            [JsonPropertyName("items")] public Dictionary<string, MapItemLocale> Items { get; set; } = new();
            [JsonPropertyName("pools")] public Dictionary<string, MapPoolLocale> Pools { get; set; } = new();
            [JsonPropertyName("banner_sources")] public Dictionary<string, MapBannerSourceLocale> BannerSources { get; set; } = new();
        }

        private sealed class MapItemLocale { [JsonPropertyName("name")] public string Name { get; set; } = ""; }

        private sealed class MapPoolLocale
        {
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("group_label")] public string? GroupLabel { get; set; }
            [JsonPropertyName("title")] public string? Title { get; set; }
        }

        private sealed class MapBannerSourceLocale { [JsonPropertyName("title")] public string Title { get; set; } = ""; }

        // ── AvatarDataTable.json（UE 数据表导出：rows[RowKey].icon/BigImage.AssetPathName） ──
        private sealed class AvatarDoc
        {
            [JsonPropertyName("rows")] public Dictionary<string, AvatarRow>? Rows { get; set; }
        }

        private sealed class AvatarRow
        {
            [JsonPropertyName("BigImage")] public AvatarAsset? BigImage { get; set; }
            [JsonPropertyName("icon")] public AvatarAsset? Icon { get; set; }
        }

        private sealed class AvatarAsset
        {
            [JsonPropertyName("AssetPathName")] public string AssetPathName { get; set; } = "";
        }
        // ── DT_ForkItemData.json（UE 导出数组：[0].Properties.Rows[id]）──
        private sealed class ForkDoc { [JsonPropertyName("Properties")] public ForkProps? Properties { get; set; } }
        private sealed class ForkProps { [JsonPropertyName("Rows")] public Dictionary<string, ForkRow>? Rows { get; set; } }
        private sealed class ForkRow
        {
            [JsonPropertyName("ItemIcon")] public AvatarAsset? ItemIcon { get; set; }
            [JsonPropertyName("ItemIconSmall")] public AvatarAsset? ItemIconSmall { get; set; }
            [JsonPropertyName("ItemName")] public ForkNameText? ItemName { get; set; }
            [JsonPropertyName("ItemQuality")] public string? ItemQuality { get; set; }
        }
        private sealed class ForkNameText { [JsonPropertyName("SourceString")] public string SourceString { get; set; } = ""; }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_gate)
            {
                if (_loaded) return;
                try
                {
                    Load();
                    _available = _loadError is null;
                }
                catch (Exception ex)
                {
                    _loadError = ex.Message;
                    _available = false;
                }
                _loaded = true;
            }
        }

        private static void Load()
        {
            var dir = ProbeDir(out var expectedDir);
            if (dir is null)
            {
                _loadError = $"未找到地图目录 {expectedDir} core.json 与 locales/zh-CN.json";
                return;
            }

            var corePath = Path.Combine(dir, "core.json");
            if (!File.Exists(corePath)) { _loadError = $"未找到 {corePath}"; return; }

            // 语言包探测（支持平铺与 locales\ 子文件夹两种布局；zh-CN 优先）
            string localePath, localeName;
            var localeCandidates = new[]
            {
                (Path.Combine(dir, "zh-CN.json"), "zh-CN"),
                (Path.Combine(dir, "locales", "zh-CN.json"), "zh-CN"),
                (Path.Combine(dir, "zh-Hans.json"), "zh-Hans"),
                (Path.Combine(dir, "locales", "zh-Hans.json"), "zh-Hans"),
            };
            var localeHit = localeCandidates.FirstOrDefault(c => File.Exists(c.Item1));
            if (localeHit.Item1 is null)
            {
                _loadError = $"未找到语言包：{dir} 及其 locales\\ 子目录下都没有 zh-CN.json / zh-Hans.json";
                return;
            }
            localePath = localeHit.Item1;
            localeName = localeHit.Item2;

            var core = ParseJson<MapCoreDocument>(corePath, "core.json");
            var locale = ParseJson<MapLocaleDocument>(localePath, Path.GetFileName(localePath));

            ValidateSchema("core.json", core.Schema, core.SchemaVersion, CoreSchema, CoreVersion);

            var folded = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in core.Items.Keys)
            {
                var key = id.ToLowerInvariant();
                if (!folded.TryAdd(key, id))
                    throw new InvalidOperationException($"core.json: case-insensitive duplicate item_id: {folded[key]} 与 {id}");
            }

            foreach (var kv in core.BannerSources)
            {
                if (kv.Value.SourceId != kv.Key)
                    throw new InvalidOperationException($"core.json: banner source key/id mismatch: {kv.Key}");
                if (!core.Pools.ContainsKey(kv.Value.PoolId))
                    throw new InvalidOperationException($"core.json: banner source targets unknown pool: {kv.Key} -> {kv.Value.PoolId}");
            }

            ValidateSchema(Path.GetFileName(localePath), locale.Schema, locale.SchemaVersion, LocaleSchema, LocaleVersion);
            if (locale.Locale != localeName)
                throw new InvalidOperationException($"{localeName}: locale 字段是 {locale.Locale}");
            if (!SameKeys(core.Items, locale.Items))
                throw new InvalidOperationException($"{localeName}: locale keys must exactly match core: items");
            if (!SameKeys(core.Pools, locale.Pools))
                throw new InvalidOperationException($"{localeName}: locale keys must exactly match core: pools");
            if (!SameKeys(core.BannerSources, locale.BannerSources))
                throw new InvalidOperationException($"{localeName}: locale keys must exactly match core: banner_sources");

            var items = new Dictionary<string, MappedItem>(StringComparer.Ordinal);
            foreach (var kv in core.Items)
            {
                var name = locale.Items[kv.Key].Name;
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidOperationException($"{localeName}: empty item name: {kv.Key}");
                items[kv.Key] = new MappedItem(name, kv.Value.Rarity, kv.Value.Category, kv.Value.Subtype, kv.Value.AssetRefs);
            }

            var pools = new Dictionary<string, MappedPool>(StringComparer.Ordinal);
            foreach (var kv in core.Pools)
            {
                var loc = locale.Pools[kv.Key];
                if (string.IsNullOrWhiteSpace(loc.Name))
                    throw new InvalidOperationException($"{localeName}: empty pool name: {kv.Key}");
                pools[kv.Key] = new MappedPool(loc.Name, loc.GroupLabel, loc.Title);
            }

            var banners = new Dictionary<string, MappedBanner>(StringComparer.Ordinal);
            foreach (var kv in core.BannerSources)
            {
                var loc = locale.BannerSources[kv.Key];
                if (string.IsNullOrWhiteSpace(loc.Title))
                    throw new InvalidOperationException($"{localeName}: empty banner title: {kv.Key}");
                banners[kv.Key] = new MappedBanner(kv.Value.PoolId, kv.Value.PoolKind, kv.Value.BannerType, loc.Title);
            }

            _items = items;
            _pools = pools;
            _banners = banners;
            _aliases = core.ItemAliases ?? new Dictionary<string, string>();
            _itemsByFolded = folded;
            _loadError = null;
        }

        private static string? ProbeDir(out string expectedDir)
        {
            expectedDir = Path.Combine(AppContext.BaseDirectory, "Assets", "maps");
            var candidates = new[]
            {
                expectedDir,                                                     // bin 输出目录（csproj 复制后）
                Path.Combine(AppContext.BaseDirectory, "Assets", "Maps"),        // 大小写变体双保险
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "maps"), // 源码目录（VS 调试运行）
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Maps"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "名称对照表"),
            };
            return candidates.FirstOrDefault(Directory.Exists);
        }

        private static T ParseJson<T>(string path, string sourceName)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path))
                       ?? throw new InvalidOperationException($"{sourceName}: 文档为空");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{sourceName}: {ex.Message}");
            }
        }

        private static void ValidateSchema(string source, string actual, long version, string expected, long expectedVersion)
        {
            if (actual != expected || version != expectedVersion)
                throw new InvalidOperationException($"{source}: expected {expected} schema_version {expectedVersion}");
        }

        private static bool SameKeys<TCore, TLocale>(Dictionary<string, TCore> core, Dictionary<string, TLocale> locale)
            => core.Count == locale.Count && !core.Keys.Except(locale.Keys, StringComparer.Ordinal).Any();

        private static string CanonicalItemId(string itemId)
        {
            if (_aliases.TryGetValue(itemId, out var alias)) return alias;
            if (_items.ContainsKey(itemId)) return itemId;
            return _itemsByFolded.TryGetValue(itemId.ToLowerInvariant(), out var folded) ? folded : itemId;
        }

        public static string ItemName(string itemId)
        {
            EnsureLoaded();
            if (!_available || string.IsNullOrEmpty(itemId)) return itemId ?? "";
            var canonical = CanonicalItemId(itemId);
            if (_items.TryGetValue(canonical, out var item)) return item.Name;
            // [弧盘兜底] core.json 未收录的新弧盘 → DT_ForkItemData 中文名
            EnsureForkTableLoaded();
            if (_forkNameByItemId.TryGetValue(itemId, out var n)) return n;          // ★ 改动①：三元式拆为 if
            if (_forkNameByItemId.TryGetValue(canonical, out n)) return n;
            // ★ LogMiss：三级全 miss → 登记 map_misses.log（人工补录待办清单）
            LogMiss("ITEM", itemId, "纯数字→core.json items+locales；fork_→DT_ForkItemData Rows");
            return itemId;
        }

        public static int ItemRarity(string itemId)
        {
            EnsureLoaded();
            if (!_available || string.IsNullOrEmpty(itemId)) return 0;
            var canonical = CanonicalItemId(itemId);
            if (_items.TryGetValue(canonical, out var item)) return item.Rarity;
            // [弧盘兜底]弧盘星级（ORANGE=5/PURPLE=4/BLUE=3，已对 core.json 验证）
            EnsureForkTableLoaded();
            return _forkRarityByItemId.TryGetValue(itemId, out var r) ? r
                 : _forkRarityByItemId.TryGetValue(canonical, out r) ? r
                 : 0;
        }


        public static string? BannerTitle(string? lotteryId, string? poolId)
        {
            EnsureLoaded();
            if (!_available) return null;
            // ★ 改动②：fork 池判定抽成局部量（行为与原短路条件完全一致）
            var isForkPool = !string.IsNullOrEmpty(poolId)
                          && poolId.StartsWith("ForkLottery_", StringComparison.Ordinal);
            if (isForkPool
                && _banners.TryGetValue(poolId, out var forkBanner)
                && forkBanner.PoolId == poolId)
                return forkBanner.Title;

            if (string.IsNullOrEmpty(lotteryId))
            {
                // ★ LogMiss：弧盘池横幅未收录（lotteryId 为空，只能以 poolId 登记）
                if (isForkPool)
                    LogMiss("BANNER", poolId, "core.json banner_sources(ForkLottery_*) + locales 横幅名");
                return null;
            }
            if (!_banners.TryGetValue(lotteryId, out var banner))
            {
                // ★ LogMiss：横幅未收录
                LogMiss("BANNER", lotteryId, "core.json banner_sources + locales 横幅名");
                return null;
            }
            if (banner.PoolId != poolId) return null;
            return banner.Title;
        }

        public static string PoolLabel(string? poolId)
        {
            EnsureLoaded();
            if (!_available || string.IsNullOrEmpty(poolId)) return poolId ?? "";
            if (_pools.TryGetValue(poolId, out var pool))                            // ★ 改动③：三元式拆为 if
                return NonEmpty(pool.Title) ?? pool.Name;
            LogMiss("POOL", poolId, "core.json pools + locales 池名");
            return poolId;
        }

        /// <summary>头像图 本地 Assets\Avatars\ 有同名文件则优先本地。</summary>
        public static string? ItemAvatarUrl(string itemId)
        {
            EnsureLoaded();
            if (!_available || string.IsNullOrEmpty(itemId)) return null;
            if (!_items.TryGetValue(CanonicalItemId(itemId), out var item) || item.AssetRefs is null) return null;

            var raw = item.AssetRefs.TryGetValue("head_icon", out var head) && !string.IsNullOrEmpty(head) ? head
                    : item.AssetRefs.TryGetValue("icon", out var icon) ? icon
                    : null;
            if (string.IsNullOrEmpty(raw)) return null;

            var repoPath = raw.StartsWith("/Game/UI/", StringComparison.Ordinal) ? raw["/Game/UI/".Length..] : raw.TrimStart('/');
            var last = repoPath[(repoPath.LastIndexOf('/') + 1)..];
            var dot = last.IndexOf('.');
            if (dot > 0) last = last[..dot];

            var local = Path.Combine(AppContext.BaseDirectory, "Assets", "Avatars", last + ".png");
            if (File.Exists(local)) return local;

            return $"https://raw.githubusercontent.com/Waifus-Grace/NTE_Assets/main/UI_Icon/{repoPath[..repoPath.LastIndexOf('/')]}/{last}.png";
        }

        // ══════════ 头像本地路径（双源，全本地零网络） ══════════
        // ① 纯数字 ID → AvatarDataTable.json（UE 数据表，覆盖含 CustomAvatar/时装的全量角色头像）
        //    查找序 256 → 200 → 100；剥尾 _1 兜底（引用 player_003_256_1 ↔ 文件 player_003_256.png）
        // ② 查不到 / fork_* 弧盘 → 回落 core.json asset_refs → 头像目录 + Assets\Avatars 平铺
        //    （fork_*_100.png / player_003x100.png 直接命中；x100/_100/_256 后缀互备）
        /// <summary>头像图本地绝对路径；找不到返回 null（UI 显示空白占位，不报错）。</summary>
        public static string? ItemAvatarPath(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            EnsureLoaded();
            EnsureAvatarLoaded();

            // ① 纯数字 ID：AvatarDataTable.json 直查
            if (itemId.All(char.IsDigit))
            {
                if (_avatarByItemId.TryGetValue(itemId, out var name) || _avatarByItemId.TryGetValue(CanonicalItemId(itemId), out name))
                {
                    var hit = FindInAvatarDirs(name);
                    if (hit is not null) return hit;
                }
            }

            // ② 回落 core.json asset_refs（fork_* 弧盘固定走这里）
            if (_available && _items.TryGetValue(CanonicalItemId(itemId), out var item) && item.AssetRefs is not null)
            {
                var raw = item.AssetRefs.TryGetValue("head_icon", out var head) && !string.IsNullOrEmpty(head) ? head
                        : item.AssetRefs.TryGetValue("icon", out var icon) ? icon : null;
                var name2 = ExtractAssetFileName(raw);
                if (!string.IsNullOrEmpty(name2))
                {
                    var hit2 = FindInAvatarDirs(name2) ?? FindFlat(name2);
                    if (hit2 is not null) return hit2;
                }
            }

            // ③ [弧盘兜底] DT_ForkItemData.json：core.json 未收录的新弧盘（worldrain/moon/Whale/Candy 系列…）
            EnsureForkTableLoaded();
            var canonicalId = CanonicalItemId(itemId);
            if (_forkIconByItemId.TryGetValue(itemId, out var forkIcon) ||
                _forkIconByItemId.TryGetValue(canonicalId, out forkIcon))
            {
                var hit3 = FindFlat(forkIcon);
                if (hit3 is not null) return hit3;
            }
            // ★ 改动④：三级头像链全 miss → 登记 map_misses.log（PNG 按 core.json/DT 表引用尾段名补）
            LogMiss("ICON", itemId, "PNG 按引用尾段名改名，放 Assets\\Fork\\");
            return null;
        }


        private static void EnsureAvatarLoaded()
        {
            if (_avatarLoaded) return;
            lock (_gate)
            {
                if (_avatarLoaded) return;
                try
                {
                    var dir = FindAvatarDir();
                    if (dir is not null)
                    {
                        _avatarDirs = new[]
                        {
                            Path.Combine(dir, "256"),
                            Path.Combine(dir, "200"),
                            Path.Combine(dir, "100"),
                        };
                        var jsonPath = Path.Combine(dir, "AvatarDataTable.json");
                        if (File.Exists(jsonPath))
                        {
                            var doc = JsonSerializer.Deserialize<AvatarDoc>(File.ReadAllText(jsonPath));
                            if (doc?.Rows is not null)
                            {
                                foreach (var kv in doc.Rows)
                                {
                                    // 只收纯数字 Row key（角色头像）；Like_/Fashion_/Combat_ 等前缀跳过
                                    if (kv.Key.Length == 0 || !kv.Key.All(char.IsDigit)) continue;
                                    var asset = kv.Value.Icon ?? kv.Value.BigImage;
                                    var name = ExtractAssetFileName(asset?.AssetPathName);
                                    if (!string.IsNullOrEmpty(name))
                                        _avatarByItemId[kv.Key] = name;
                                }
                            }
                        }
                    }
                }
                catch { /* 头像表缺失/损坏不伤主地图与引擎 */ }
                _avatarLoaded = true;
            }
        }

        /// <summary>[弧盘兜底] 加载 DT_ForkItemData.json：id → 图标基名 + 中文名 + 星级。</summary>
        private static void EnsureForkTableLoaded()
        {
            if (_forkTableLoaded) return;
            lock (_gate)
            {
                if (_forkTableLoaded) return;
                try
                {
                    var dirs = new[]
                    {
                        Path.Combine(AppContext.BaseDirectory, "Assets", "Fork"),
                        Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Fork"),
                    };
                    var jsonPath = dirs.Select(d => Path.Combine(d, "DT_ForkItemData.json")).FirstOrDefault(File.Exists);
                    if (jsonPath is not null)
                    {
                        var docs = JsonSerializer.Deserialize<List<ForkDoc>>(File.ReadAllText(jsonPath));
                        var rows = docs?.FirstOrDefault()?.Properties?.Rows;
                        if (rows is not null)
                            foreach (var kv in rows)
                            {
                                var asset = kv.Value.ItemIconSmall ?? kv.Value.ItemIcon;
                                var icon = ExtractAssetFileName(asset?.AssetPathName);
                                if (!string.IsNullOrEmpty(icon)) _forkIconByItemId[kv.Key] = icon;
                                var cn = kv.Value.ItemName?.SourceString;
                                if (!string.IsNullOrWhiteSpace(cn)) _forkNameByItemId[kv.Key] = cn;
                                _forkRarityByItemId[kv.Key] = kv.Value.ItemQuality switch
                                {
                                    "EItemQuality::ITEM_QUALITY_ORANGE" => 5,
                                    "EItemQuality::ITEM_QUALITY_PURPLE" => 4,
                                    "EItemQuality::ITEM_QUALITY_BLUE" => 3,
                                    _ => 0,
                                };
                            }
                    }
                }
                catch { /* 表缺失/损坏不伤主链路 */ }
                _forkTableLoaded = true;
            }
        }


        private static string? FindAvatarDir()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "AvatarDataTable"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "AvatarDataTable"),
            };
            return candidates.FirstOrDefault(Directory.Exists);
        }

        /// <summary>UE AssetPathName（/Game/.../目录/名.名）→ 资源名（取末段首个 . 前部分）。</summary>
        private static string? ExtractAssetFileName(string? assetPathName)
        {
            if (string.IsNullOrEmpty(assetPathName)) return null;
            var last = assetPathName[(assetPathName.LastIndexOf('/') + 1)..];
            var dot = last.IndexOf('.');
            return dot > 0 ? last[..dot] : last;
        }

        /// <summary>按 256→200→100 目录序查找；变体：原样 / 剥尾 _1 / x100·_100·_256 后缀互备。</summary>
        private static string? FindInAvatarDirs(string baseName)
        {
            if (_avatarDirs is null) return null;
            foreach (var candidate in NameVariants(baseName))
                foreach (var dir in _avatarDirs)
                {
                    var p = Path.Combine(dir, candidate + ".png");
                    if (File.Exists(p)) return p;
                }
            return null;
        }

        // ★★★ 改动⑤：未收录 ID 登记器（LogMiss）——查表 miss 时写一行 exe 旁 map_misses.log ★★★
        // 用途：人工补录待办清单。同一 kind|id 每次启动只记一行；文件可随时删除，下次 miss 自动重建。
        private static readonly object _missLock = new();
        private static readonly HashSet<string> _missLogged = new();

        /// <summary>地图查表未命中时登记到 exe 旁 map_misses.log，作为人工补录待办清单。</summary>
        public static void LogMiss(string kind, string id, string hint)
        {
            try
            {
                var key = kind + "|" + id;
                lock (_missLock)
                {
                    if (!_missLogged.Add(key)) return;   // 去重：一条 ID 只记一次，不刷屏
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{kind}\t{id}\t=> {hint}{Environment.NewLine}";
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "map_misses.log"), line);
                }
            }
            catch { /* 日志失败静默，绝不影响主流程 */ }
        }

        /// <summary>平铺目录候选：Assets\Avatars\+ Assets\Fork\（弧盘全套）+ LotteryDiceSkin\（骰盘皮肤）。</summary>
        private static string? FindFlat(string baseName)
        {
            var baseDir = AppContext.BaseDirectory;
            var dirs = new[]
            {
                Path.Combine(baseDir, "Assets", "Avatars"),
                Path.Combine(baseDir, "Assets", "Fork"),
                Path.Combine(baseDir, "Assets", "AvatarDataTable", "LotteryDiceSkin"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Avatars"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Fork"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "AvatarDataTable", "LotteryDiceSkin"),
            };
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var candidate in NameVariants(baseName))
                {
                    var p = Path.Combine(dir, candidate + ".png");
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }


        private static IEnumerable<string> NameVariants(string baseName)
        {
            yield return baseName;
            if (baseName.EndsWith("_1", StringComparison.Ordinal))
                yield return baseName[..^2];                       // 引用带 _1 ↔ 文件无（AvatarDataTable 对齐规律）
            foreach (var suffix in new[] { "x100", "_100", "_256" })
            {
                if (baseName.EndsWith(suffix, StringComparison.Ordinal))
                    yield return baseName[..^suffix.Length];       // 去后缀找大图
                else
                    yield return baseName + suffix;                // 加后缀找小图
            }
        }

        private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
