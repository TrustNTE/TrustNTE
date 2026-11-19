using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using TrustNTE.Models.Gacha;
using TrustNTE.Services.Gacha.Maps;
using TrustNTE.Services.Gacha.Protocol;
using TrustNTE.Services.Interfaces;

namespace TrustNTE.Services.Gacha
{
    /// <summary>
    /// 抓包引擎 Npcap(SharpPcap) 后端实现（nte-capture 规格 + 两层重组增强 v4）。
    /// [v4修复] ScanFlow 改走完整 ScanMarker(raw+shift8 全视图)——shift 视图块跨 TCP 段后重组可参与解析;
    /// [v4修复] OnPacketArrival 空 catch 改为计数+首条记录+总账输出。
    /// </summary>
    public sealed class GachaCaptureService : IGachaCaptureService
    {
        private readonly ILogService _log;
        private readonly List<LibPcapLiveDevice> _openDevices = new();

        public bool IsRunning { get; private set; }

        public event Action<string>? EngineInfoChanged;
        public event Action<int, int>? CountersUpdated;
        public event Action<GachaRecord>? RecordDecoded;

        private long _captured;
        private long _decoded;
        private long _dedupStamp;
        private long _okBlocks;
        private long _failCalls;
        private long _newRecords;
        private long _packetErrs;                 // [v4修复]
        private string _firstPacketErr = "";      // [v4修复]
        private static bool _mapWarnLogged;

        private readonly ConcurrentDictionary<string, byte> _seenRecordIds = new();
        private readonly ConcurrentDictionary<int, byte> _extraPorts = new();

        private int _dedupHash = int.MinValue;
        private CancellationTokenSource? _cts;

        private readonly object _flowLock = new();
        private readonly Dictionary<string, TcpFlow> _tcpFlows = new();
        private readonly Dictionary<string, IpFrag> _ipFrags = new();
        private string _portsCsv = "";

        public GachaCaptureService(ILogService log) => _log = log;

        public async Task StartAsync(GachaCaptureConfig config, CancellationToken token)
        {
            if (IsRunning) return;

            if (Process.GetProcessesByName("HTGame").Length == 0)
                throw new InvalidOperationException("未检测到游戏进程 HTGame.exe —— 请先启动游戏并进入抽卡界面后重试");

            IReadOnlyList<LibPcapLiveDevice> devices;
            try { devices = LibPcapLiveDeviceList.Instance; }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Npcap 不可用 —— 请在风险提示页点击「下载 Npcap」安装后重试", ex);
            }
            if (devices.Count == 0)
                throw new InvalidOperationException("没有可用的网络适配器 —— Npcap 可能安装异常，建议重装");

            var hasPppoe = NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.Description.Contains("PPPoE", StringComparison.OrdinalIgnoreCase));
            if (hasPppoe)
                _log.LogDebug("[抽卡] 检测到 PPPoE 环境（解析器已兼容）");

            var targetNic = string.IsNullOrWhiteSpace(config.NicName) ? null : config.NicName;
            _openDevices.Clear();
            foreach (var dev in devices)
            {
                if (targetNic is not null && dev.Name != targetNic) continue;
                try
                {
                    dev.Open(new DeviceConfiguration());
                    dev.Filter = "udp or tcp";
                    dev.OnPacketArrival += OnPacketArrival;
                    dev.StartCapture();
                    _openDevices.Add(dev);
                }
                catch (Exception ex)
                {
                    _log.LogDebug($"[抽卡] 网卡 {(string.IsNullOrEmpty(dev.Description) ? dev.Name : dev.Description)} 打开失败（跳过）：{ex.Message}");
                }
            }
            if (_openDevices.Count == 0)
                throw new InvalidOperationException(
                    targetNic is null
                        ? "所有网卡打开失败 —— 请确认程序以管理员运行且 Npcap 安装正常"
                        : "指定网卡打开失败 —— 请点「刷新」重选网卡，或改选「全部网卡」");

            var ports = string.Join('/', config.GetPorts());
            var nicTag = targetNic is null ? $"全部 {_openDevices.Count} 张网卡" : $"指定网卡 · 仅 {_openDevices.Count} 张";
            EngineInfoChanged?.Invoke($"引擎:Npcap · {nicTag} · 端口表 {ports}");
            _log.LogDebug($"[抽卡] 抓包会话启动 · Npcap · {nicTag} · 目标端口 {ports}");
            _log.LogWarning($"[解包] 解析器版本 {GachaBlockParser.ParserVersion}（应为 P12）");

            _portsCsv = $",{string.Join(',', config.GetPorts())},";
            _extraPorts.Clear();
            lock (_flowLock) { _tcpFlows.Clear(); _ipFrags.Clear(); }

            IsRunning = true;
            Interlocked.Exchange(ref _captured, 0);
            Interlocked.Exchange(ref _decoded, 0);
            Interlocked.Exchange(ref _okBlocks, 0);
            Interlocked.Exchange(ref _failCalls, 0);
            Interlocked.Exchange(ref _newRecords, 0);
            Interlocked.Exchange(ref _packetErrs, 0);           // [v4修复]
            Volatile.Write(ref _firstPacketErr, "");            // [v4修复]
            _seenRecordIds.Clear();
            _dedupHash = int.MinValue;
            _dedupStamp = 0;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _cts.Token.Register(() => _ = StopAsync());

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(500, _cts.Token);
                        CountersUpdated?.Invoke((int)Interlocked.Read(ref _captured),
                                                (int)Interlocked.Read(ref _decoded));
                    }
                }
                catch (OperationCanceledException) { }
            }, _cts.Token);

            await Task.CompletedTask;
        }

        public Task StopAsync()
        {
            if (!IsRunning && _openDevices.Count == 0) return Task.CompletedTask;

            IsRunning = false;
            try { _cts?.Cancel(); } catch { }

            foreach (var dev in _openDevices)
            {
                try
                {
                    dev.OnPacketArrival -= OnPacketArrival;
                    dev.StopCapture();
                    dev.Close();
                }
                catch { /* 设备已死时安全吞 */ }
            }
            _openDevices.Clear();
            lock (_flowLock) { _tcpFlows.Clear(); _ipFrags.Clear(); }

            _log.LogWarning($"[解包] 会话总账 · 成功块 {Interlocked.Read(ref _okBlocks)} · 解析失败 {Interlocked.Read(ref _failCalls)} 次 · 新记录 {Interlocked.Read(ref _newRecords)} 条");
            // [v4修复] 包级异常不再静默吞掉
            var errs = Interlocked.Read(ref _packetErrs);
            if (errs > 0)
                _log.LogWarning($"[解包] 包级异常总账 {errs} 次 · 首条: {Volatile.Read(ref _firstPacketErr)}");
            _log.LogDebug($"[抽卡] 抓包会话结束 · 捕获 {Interlocked.Read(ref _captured)} 包 · 解码 {Interlocked.Read(ref _decoded)} 包");
            return Task.CompletedTask;
        }

        // ══════════ 包处理 ══════════

        private void OnPacketArrival(object sender, PacketCapture e)
        {
            Interlocked.Increment(ref _captured);
            try
            {
                var data = e.GetPacket().Data;
                var link = rawLinkOffset(e.GetPacket().LinkLayerType);
                if (link < 0) return;

                var ipOffset = ParseLinkToIp(data, link);
                if (ipOffset < 0) return;

                // [R1] IPv4 分片：先重组再进 L4（非分片包零开销直通）
                if ((data[ipOffset] >> 4) == 4 && IsFragmented(data, ipOffset))
                {
                    var full = TryDefragment(data, ipOffset);
                    if (full is null) return;
                    data = full;
                    ipOffset = 0;
                }

                ProcessIpPacket(data, ipOffset);
            }
            catch (Exception ex)   // [v4修复] 不再空吞:计数+首条,总账输出
            {
                var n = Interlocked.Increment(ref _packetErrs);
                if (n == 1) Volatile.Write(ref _firstPacketErr, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static int ParseLinkToIp(byte[] data, int link)
        {
            if (link == 14)
            {
                if (data.Length < 14) return -1;
                var etherType = (data[12] << 8) | data[13];
                var off = 14;
                while (etherType is 0x8100 or 0x88A8 or 0x9100)
                {
                    if (data.Length < off + 4) return -1;
                    etherType = (data[off + 2] << 8) | data[off + 3];
                    off += 4;
                }
                if (etherType is 0x0800 or 0x86DD) return off;
                if (etherType == 0x8864)
                {
                    if (data.Length < off + 8 || data[off + 1] != 0) return -1;
                    var pppProto = (data[off + 6] << 8) | data[off + 7];
                    return pppProto is 0x0021 or 0x0057 ? off + 8 : -1;
                }
                return -1;
            }
            return link;
        }

        private static bool IsFragmented(byte[] data, int ipOffset)
        {
            if (data.Length < ipOffset + 8) return false;
            var flagsFrag = (data[ipOffset + 6] << 8) | data[ipOffset + 7];
            return (flagsFrag & 0x3FFF) != 0;   // MF 或片偏移非 0（DF 不算）
        }

        private byte[]? TryDefragment(byte[] data, int ipOffset)
        {
            var ihl = (data[ipOffset] & 0x0F) * 4;
            var totalIp = (data[ipOffset + 2] << 8) | data[ipOffset + 3];
            var ipId = (data[ipOffset + 4] << 8) | data[ipOffset + 5];
            var flagsFrag = (data[ipOffset + 6] << 8) | data[ipOffset + 7];
            var moreFrag = (flagsFrag & 0x2000) != 0;
            var fragOff = (flagsFrag & 0x1FFF) * 8;
            var proto = data[ipOffset + 9];
            var src = $"{data[ipOffset + 12]}.{data[ipOffset + 13]}.{data[ipOffset + 14]}.{data[ipOffset + 15]}";
            var dst = $"{data[ipOffset + 16]}.{data[ipOffset + 17]}.{data[ipOffset + 18]}.{data[ipOffset + 19]}";
            var key = $"{src}|{dst}|{ipId}|{proto}";

            var copyLen = Math.Min(totalIp, data.Length) - ipOffset - ihl;
            if (copyLen <= 0) return null;
            var frag = new byte[copyLen];
            Array.Copy(data, ipOffset + ihl, frag, 0, copyLen);

            lock (_flowLock)
            {
                var now = Environment.TickCount64;
                if (_ipFrags.Count > 256)
                    foreach (var k in _ipFrags.Where(kv => now - kv.Value.FirstSeen > 15_000).Select(kv => kv.Key).ToList())
                        _ipFrags.Remove(k);

                if (!_ipFrags.TryGetValue(key, out var entry) || now - entry.FirstSeen > 15_000)
                {
                    entry = new IpFrag { FirstSeen = now };
                    _ipFrags[key] = entry;
                }
                if (entry.Parts.Count > 64) return null;

                if (fragOff == 0)
                {
                    entry.HeaderLen = ihl;
                    entry.Header = new byte[ihl];
                    Array.Copy(data, ipOffset, entry.Header, 0, ihl);
                }
                if (!moreFrag && fragOff > 0)
                    entry.TotalLen = fragOff + frag.Length;
                if (!entry.Parts.Any(p => p.Off == fragOff)) entry.Parts.Add((fragOff, frag));

                if (entry.TotalLen < 0 || entry.Header is null) return null;

                entry.Parts.Sort((a, b) => a.Off - b.Off);
                var cursor = 0;
                foreach (var p in entry.Parts)
                {
                    if (p.Off > cursor) return null;         // 有洞，继续等
                    if (p.Off + p.Data.Length > cursor) cursor = p.Off + p.Data.Length;
                }
                if (cursor < entry.TotalLen) return null;

                var full = new byte[entry.TotalLen];
                Array.Copy(entry.Header, 0, full, 0, entry.HeaderLen);
                foreach (var p in entry.Parts)
                {
                    if (p.Off >= entry.TotalLen) continue;
                    Array.Copy(p.Data, 0, full, p.Off, Math.Min(p.Data.Length, entry.TotalLen - p.Off));
                }
                _ipFrags.Remove(key);
                return full;
            }
        }

        private void ProcessIpPacket(byte[] data, int ipOffset)
        {
            if (data.Length < ipOffset + 20) return;
            var version = data[ipOffset] >> 4;
            int l4Offset;
            bool isUdp;
            string srcIp, dstIp;
            if (version == 4)
            {
                var ihl = (data[ipOffset] & 0x0F) * 4;
                if (ihl < 20 || data.Length < ipOffset + ihl + 20) return;
                l4Offset = ipOffset + ihl;
                var proto = data[ipOffset + 9];
                if (proto == 17) isUdp = true;
                else if (proto == 6) isUdp = false;
                else return;
                srcIp = $"{data[ipOffset + 12]}.{data[ipOffset + 13]}.{data[ipOffset + 14]}.{data[ipOffset + 15]}";
                dstIp = $"{data[ipOffset + 16]}.{data[ipOffset + 17]}.{data[ipOffset + 18]}.{data[ipOffset + 19]}";
            }
            else if (version == 6)
            {
                if (data.Length < ipOffset + 40 + 20) return;
                l4Offset = ipOffset + 40;
                var nextHeader = data[ipOffset + 6];
                while (nextHeader is 0 or 43 or 60)
                {
                    if (data.Length < l4Offset + 8) return;
                    nextHeader = data[l4Offset];
                    l4Offset += (data[l4Offset + 1] + 1) * 8;
                }
                if (nextHeader == 44)        // IPv6 分片头：原文行为保留（跳过，放弃重组）
                {
                    if (data.Length < l4Offset + 8) return;
                    nextHeader = data[l4Offset];
                    l4Offset += 8;
                }
                if (nextHeader == 17) isUdp = true;
                else if (nextHeader == 6) isUdp = false;
                else return;
                srcIp = BitConverter.ToString(data, ipOffset + 8, 16);
                dstIp = BitConverter.ToString(data, ipOffset + 24, 16);
            }
            else return;

            ReadOnlySpan<byte> payload;
            int sport, dport;
            long tcpSeq = 0;
            if (isUdp)
            {
                if (data.Length < l4Offset + 8) return;
                var udpLen = (data[l4Offset + 4] << 8) | data[l4Offset + 5];
                if (udpLen < 8) return;
                var end = Math.Min(l4Offset + udpLen, data.Length);
                if (end <= l4Offset + 8) return;
                payload = data.AsSpan(l4Offset + 8, end - l4Offset - 8);
                sport = (data[l4Offset] << 8) | data[l4Offset + 1];
                dport = (data[l4Offset + 2] << 8) | data[l4Offset + 3];
            }
            else
            {
                if (data.Length < l4Offset + 20) return;
                var tcpHeaderLen = ((data[l4Offset + 12] >> 4) & 0x0F) * 4;
                if (tcpHeaderLen < 20 || data.Length < l4Offset + tcpHeaderLen) return;
                tcpSeq = (long)(((uint)data[l4Offset + 4] << 24) | ((uint)data[l4Offset + 5] << 16)
                              | ((uint)data[l4Offset + 6] << 8) | data[l4Offset + 7]);
                payload = data.AsSpan(l4Offset + tcpHeaderLen, data.Length - l4Offset - tcpHeaderLen);
                sport = (data[l4Offset] << 8) | data[l4Offset + 1];
                dport = (data[l4Offset + 2] << 8) | data[l4Offset + 3];
            }
            if (payload.Length <= 0) return;

            // ── 去重窗 250ms（原文保留）──
            var now = Environment.TickCount64;
            var hash = FnvHash(payload);
            hash = hash * 16777619 ^ (isUdp ? 17 : 6);
            hash = hash * 16777619 ^ sport;
            hash = hash * 16777619 ^ dport;
            if (hash == Volatile.Read(ref _dedupHash) &&
                now - Volatile.Read(ref _dedupStamp) < 250) return;
            Volatile.Write(ref _dedupHash, hash);
            Volatile.Write(ref _dedupStamp, now);

            // ── 单段保底扫描（原文语义：raw + shift8 全视图）──
            var (hit, view) = ScanMarker(payload);
            if (hit)
            {
                if (!isUdp && !PortGated(sport, dport))
                {
                    // [R2] 自动发现：这条流携带完整抽卡块 → 端口加入重组白名单
                    _extraPorts[sport] = 0;
                    _extraPorts[dport] = 0;
                    _log.LogWarning($"[解包] 发现携带抽卡块的端口 {sport}/{dport} —— 已加入流重组");
                }
                EmitParsed(payload.ToArray(), $"单段[{view}] {sport}→{dport}");
            }

            // ── [R2] TCP 流重组（端口表 ∪ 自动发现）──
            if (!isUdp && PortGated(sport, dport))
                FeedTcp($"{srcIp}:{sport}>{dstIp}:{dport}", tcpSeq, payload.ToArray());
        }

        private void FeedTcp(string flowKey, long seq, byte[] seg)
        {
            lock (_flowLock)
            {
                var now = Environment.TickCount64;
                if (_tcpFlows.Count > 512)
                    foreach (var k in _tcpFlows.Where(kv => now - kv.Value.LastActivity > 15_000).Select(kv => kv.Key).ToList())
                        _tcpFlows.Remove(k);

                if (!_tcpFlows.TryGetValue(flowKey, out var f))
                {
                    f = new TcpFlow { LastActivity = now };
                    _tcpFlows[flowKey] = f;
                    _log.LogDebug($"[解包] 建立TCP流缓冲 {flowKey}");
                }
                f.LastActivity = now;

                if (!f.Started)
                {
                    f.Started = true;
                    f.NextSeq = seq + seg.Length;
                    f.Append(seg);
                }
                else if (seq == f.NextSeq)
                {
                    f.Append(seg);
                    f.NextSeq += seg.Length;
                    DrainPending(f);
                }
                else if (seq + seg.Length <= f.NextSeq)
                {
                    return;                                      // 旧段重传
                }
                else if (seq < f.NextSeq)
                {
                    // [v3修复] 部分重叠：只接新尾巴；整段都在过去时直接丢弃（防 NextSeq 回退）
                    var skip = (int)(f.NextSeq - seq);
                    if (skip < seg.Length)
                    {
                        f.Append(seg, skip);
                        f.NextSeq = seq + seg.Length;
                        DrainPending(f);
                    }
                }
                else
                {
                    f.Pending.Add((seq, seg));
                    if (f.Pending.Count > 64)
                    {
                        _log.LogWarning($"[解包] 流 {flowKey} 乱序空洞超限，缓冲重置");
                        f.Pending.Sort((a, b) => a.Seq.CompareTo(b.Seq));
                        var first = f.Pending[0];
                        f.Buf.Clear();
                        f.NextSeq = first.Seq + first.Data.Length;
                        f.Append(first.Data);
                        f.Pending.RemoveAt(0);
                        DrainPending(f);
                    }
                }

                ScanFlow(f, flowKey);
            }
        }

        private static void DrainPending(TcpFlow f)
        {
            var progress = true;
            while (progress)
            {
                progress = false;
                for (var i = 0; i < f.Pending.Count; i++)
                {
                    var (s, d) = f.Pending[i];
                    if (s == f.NextSeq)
                    {
                        f.Append(d);
                        f.NextSeq = s + d.Length;
                        f.Pending.RemoveAt(i);
                        progress = true;
                        break;
                    }
                    if (s + d.Length <= f.NextSeq) { f.Pending.RemoveAt(i); progress = true; break; }
                    if (s < f.NextSeq)
                    {
                        var skip = (int)(f.NextSeq - s);
                        if (skip < d.Length)                     // [v3修复] 同防回退
                        {
                            f.Append(d, skip);
                            f.NextSeq = s + d.Length;
                        }
                        f.Pending.RemoveAt(i);
                        progress = true;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// [v4修复] 流重组缓冲改走完整标记扫描(raw+shift8 全视图):
        /// 旧版只扫 raw ASCII,shift 视图的块(如 shift8:3)跨 TCP 段后重组完成也不会交给解析器 → 真漏。
        /// </summary>
        private void ScanFlow(TcpFlow f, string flowKey)
        {
            if (f.Buf.Count == 0) return;
            var snap = f.Buf.ToArray();
            var (hit, view) = ScanMarker(snap);          // [v4修复]
            if (!hit) return;
            EmitParsed(snap, $"流重组[{view}] {flowKey} 缓冲{snap.Length}B");
        }

        private bool PortGated(int a, int b)
            => (_portsCsv.Length > 2 && (_portsCsv.Contains($",{a},") || _portsCsv.Contains($",{b},")))
               || _extraPorts.ContainsKey(a) || _extraPorts.ContainsKey(b);

        /// <summary>块解析 → 推送。成功/失败全部 LogWarning（永不被开发者模式闸拦）。</summary>
        private void EmitParsed(byte[] payload, string source)
        {
            List<GachaRecord> records;
            List<string> warnings;
            int blockCount;
            try
            {
                (records, warnings, blockCount) = GachaBlockParser.ParsePayloadBlocks(payload);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failCalls);
                _log.LogWarning($"[解包] ✗ {source} · 解析器意外异常 {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (blockCount > 0)
            {
                if (!GachaMapCatalog.IsAvailable && !_mapWarnLogged)
                {
                    _mapWarnLogged = true;
                    _log.LogWarning($"[抽卡] 名称地图未加载：{GachaMapCatalog.LoadError}（物品列显示原始 ID）");
                }
                var newRecords = 0;
                foreach (var record in records)
                {
                    if (!_seenRecordIds.TryAdd(record.RecordId, 0)) continue;
                    newRecords++;
                    RecordDecoded?.Invoke(record);
                }
                if (newRecords > 0)
                {
                    Interlocked.Increment(ref _decoded);
                    Interlocked.Add(ref _newRecords, newRecords);
                    Interlocked.Increment(ref _okBlocks);
                    _log.LogWarning($"[解包] ✓ {source} · 成块 {blockCount} · 记录 {records.Count} · 新 {newRecords}");
                }
                return;
            }

            if (warnings.Count > 0)
            {
                Interlocked.Increment(ref _failCalls);
                // [P12] unexpected_error(意外异常)完整死因不截断(含堆栈);parse_error(设计内淘汰)才折叠
                var first = warnings[0];
                var flat = first.Replace("\r\n", " | ");
                var isUnexpected = first.StartsWith("unexpected_error", StringComparison.Ordinal);
                _log.LogWarning(isUnexpected
                    ? $"[解包] ✗ {source} · 0块 · 死因: {flat}"
                    : $"[解包] ✗ {source} · 0块 · 失败{warnings.Count}条 · 首因: {SanitizeReason(first)}");
            }
        }

        /// <summary>去掉死因里的 "@偏移/at 位置" 尾巴，让 LogService 连发折叠能合并同类死因。</summary>
        private static string SanitizeReason(string w)
        {
            var at = w.IndexOf(" at ", StringComparison.Ordinal);
            return at > 0 ? w[..at] : w;
        }

        private static int rawLinkOffset(PacketDotNet.LinkLayers t) => (int)t switch
        {
            1 => 14,
            0 => 4,
            114 => 4,
            101 => 0,
            12 => 0,
            113 => 16,
            276 => 20,
            _ => -1
        };

        private static (bool Hit, string View) ScanMarker(ReadOnlySpan<byte> payload)
        {
            if (ContainsAscii(payload, GachaProtocolTypes.MonopolyMarker) ||
                ContainsAscii(payload, GachaProtocolTypes.ForkMarker))
                return (true, "raw");

            var count = payload.Length >= 8 ? payload.Length - 8 : 0;
            for (var shift = 1; shift <= 7; shift++)
            {
                if (ShiftedContainsBytes(payload, GachaProtocolTypes.ShiftViewByteOffset, shift, count, GachaProtocolTypes.MonopolyMarkerBytes) ||
                    ShiftedContainsBytes(payload, GachaProtocolTypes.ShiftViewByteOffset, shift, count, GachaProtocolTypes.ForkMarkerBytes))
                    return (true, $"shift8:{shift}");
            }
            return (false, "");
        }

        private static bool ShiftedContainsBytes(ReadOnlySpan<byte> data, int byteOff, int bitShift, int count, byte[] needle)
        {
            if (needle.Length == 0 || count < needle.Length) return false;
            for (var start = 0; start <= count - needle.Length; start++)
            {
                var ok = true;
                for (var i = 0; i < needle.Length; i++)
                {
                    var b = ShiftedByte(data, byteOff, bitShift, start + i);
                    if (b < 0 || b != needle[i]) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }

        private static int ShiftedByte(ReadOnlySpan<byte> data, int byteOff, int bitShift, int index)
        {
            var bitPos = (byteOff + index) * 8 + bitShift;
            var bOff = bitPos / 8;
            var bShift = bitPos % 8;
            if (bOff >= data.Length) return -1;
            var value = data[bOff] >> bShift;
            if (bShift != 0 && bOff + 1 < data.Length)
                value |= data[bOff + 1] << (8 - bShift);
            return value & 0xFF;
        }

        private static bool ContainsAscii(ReadOnlySpan<byte> payload, string ascii)
        {
            if (payload.Length < ascii.Length) return false;
            for (var i = 0; i <= payload.Length - ascii.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < ascii.Length; j++)
                {
                    if (payload[i + j] != (byte)ascii[j]) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }

        private static int FnvHash(ReadOnlySpan<byte> data)
        {
            unchecked
            {
                var hash = 2166136261;
                foreach (var b in data)
                {
                    hash ^= b;
                    hash *= 16777619;
                }
                return (int)hash;
            }
        }

        private sealed class TcpFlow
        {
            public const int CapBytes = 128 * 1024;
            public bool Started;
            public long NextSeq;
            public readonly List<byte> Buf = new();
            public readonly List<(long Seq, byte[] Data)> Pending = new();
            public long LastActivity;

            public void Append(byte[] data, int skip = 0)
            {
                for (var i = skip; i < data.Length; i++) Buf.Add(data[i]);
                if (Buf.Count > CapBytes) Buf.RemoveRange(0, Buf.Count - 65536);
            }
        }

        private sealed class IpFrag
        {
            public long FirstSeen;
            public byte[]? Header;
            public int HeaderLen;
            public int TotalLen = -1;
            public readonly List<(int Off, byte[] Data)> Parts = new();
        }
    }
}
