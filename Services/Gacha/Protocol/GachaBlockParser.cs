using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using TrustNTE.Services.Gacha.Maps;
using TrustNTE.Models.Gacha;

namespace TrustNTE.Services.Gacha.Protocol
{

    public static class GachaBlockParser
    {

        private const long DotnetEpochTicks = 621_355_968_000_000_000;
        private const long TicksPerSecond = 10_000_000;



        public static (List<GachaRecord> Records, List<string> Warnings, int BlockCount) ParsePayloadBlocks(byte[] payload)
        {
            var records = new List<GachaRecord>();
            var warnings = new List<string>();
            var blocks = 0;

            ParseView("raw", payload, records, warnings, ref blocks);

            var count = payload.Length >= 8 ? payload.Length - 8 : 0;
            for (var shift = 1; shift <= 7; shift++)
            {
                if (!ShiftedViewContainsMarker(payload, GachaProtocolTypes.ShiftViewByteOffset, shift, count)) continue;
                var data = DecodeShiftedBytes(payload, GachaProtocolTypes.ShiftViewByteOffset, shift, count);
                ParseView($"shift8:{shift}", data, records, warnings, ref blocks);
            }

            // [P5] 解析死因:①VS输出窗口(仅F5调试可见) ②落盘文件(直接跑exe也能看)
            foreach (var w in warnings)
                System.Diagnostics.Debug.WriteLine("[GachaParser] " + w);
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TrustNTE", "logs");
                System.IO.Directory.CreateDirectory(dir);
                var sb2 = new StringBuilder();
                sb2.Append($"\r\n==== {DateTime.Now:HH:mm:ss.fff} 收到一包: 成块={blocks}, 出记录={records.Count}, 失败={warnings.Count}条 ====");
                foreach (var w in warnings) sb2.Append("\r\n  ✗ ").Append(w);
                if (warnings.Count == 0 && blocks == 0) sb2.Append("\r\n  ✗ 本包里没找到任何块标记(marker疑被TCP切分)");
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "parser.log"), sb2.ToString());
            }
            catch { /* 诊断日志失败不影响解析主流程 */ }


            return (records, warnings, blocks);
        }

        private static void ParseView(string viewName, byte[] data, List<GachaRecord> records, List<string> warnings, ref int blocks)
        {
            ScanOneMarker(viewName, data, GachaProtocolTypes.MonopolyMarkerBytes, true, records, warnings, ref blocks);
            ScanOneMarker(viewName, data, GachaProtocolTypes.ForkMarkerBytes, false, records, warnings, ref blocks);
        }

        private static void ScanOneMarker(string viewName, byte[] data, byte[] marker, bool monopoly,
            List<GachaRecord> records, List<string> warnings, ref int blocks)
        {
            var pos = 0;
            int found;
            while ((found = FindBytes(data, pos, marker)) >= 0)
            {
                var markerPos = found;
                try
                {
                    var rows = monopoly
                        ? ParseMonopolyBlock(data, markerPos, marker)
                        : ParseForkBlock(data, markerPos, marker);
                    blocks++;
                    foreach (var row in rows) records.Add(BuildRecord(row));
                }
                catch (PendingOriginalException ex)
                {
                    warnings.Add($"待原文:{ex.FunctionName}");
                }
                catch (ParseInlineException ex)
                {
                    warnings.Add($"parse_error[{viewName}]@{markerPos}: {ex.Message}");
                }
                // [P6] 兜底网:任何意外异常(如 IndexOutOfRange)只丢当前块,不再拖死整个 payload
                catch (Exception ex)
                {
                    warnings.Add($"unexpected_error[{viewName}]@{markerPos}: {ex.GetType().Name}: {ex.Message}");
                }
                pos = markerPos + marker.Length;
            }
        }


        private sealed class Envelope { public string StreamKey = ""; public uint PageIndex; public bool QueryHigh; }

        private static Envelope ParseEnvelope(bool monopoly, byte[] data, int markerPos)
        {
            Envelope env;
            if (monopoly)
            {
                if (markerPos < 26) throw new ParseInlineException("invalid monopoly protocol envelope");
                var protocolConstant = RelativeU32(data, markerPos, -26);
                var queryRaw = RelativeU32(data, markerPos, -22);
                var pageRaw = RelativeU32(data, markerPos, -18);
                var blockKind = RelativeU32(data, markerPos, -14);
                var poolToken = RelativeU32(data, markerPos, -10);
                var footer = RelativeU32(data, markerPos, -6);
                if (protocolConstant != GachaProtocolTypes.ProtocolConstant ||
                    blockKind != GachaProtocolTypes.MonopolyBlockKind ||
                    footer != GachaProtocolTypes.MonopolyFooter)
                    throw new ParseInlineException("invalid monopoly protocol constants");
                env = new Envelope
                {
                    StreamKey = $"monopoly:{poolToken}",
                    PageIndex = pageRaw & 0x7fffffff,
                    QueryHigh = (queryRaw & 0x80000000) != 0,
                };
            }
            else
            {
                if (markerPos < 17) throw new ParseInlineException("invalid fork protocol envelope");
                var protocolConstant = RelativeU32(data, markerPos, -17);
                var queryRaw = RelativeU32(data, markerPos, -13);
                var pageRaw = RelativeU32(data, markerPos, -9);
                var blockKind = RelativeU32(data, markerPos, -5);
                if (protocolConstant != GachaProtocolTypes.ProtocolConstant ||
                    blockKind != GachaProtocolTypes.ForkBlockKind)
                    throw new ParseInlineException("invalid fork protocol constants");
                env = new Envelope
                {
                    StreamKey = "fork",
                    PageIndex = pageRaw & 0x7fffffff,
                    QueryHigh = (queryRaw & 0x80000000) != 0,
                };
            }

            if (!env.QueryHigh && env.PageIndex == 0)
                throw new ParseInlineException("invalid protocol segment index");
            return env;
        }


        private sealed class BlockFrame { public uint DeclaredSize; public uint RowCount; public int RowsStart; public int BlockEnd; }

        private static BlockFrame ParseBlockFrame(byte[] data, int markerPos, byte[] marker)
        {
            var pos = markerPos + marker.Length;
            if (pos < data.Length && data[pos] == 0) pos++;
            var declaredSize = U32At(data, pos + 4);
            var rowCount = U32At(data, pos + 8);
            if (rowCount > GachaProtocolTypes.MaxRowsPerBlock)
                throw new ParseInlineException($"row_count too large: {rowCount}");
            var rowsStart = pos + 12;
            if (declaredSize < 4) throw new ParseInlineException("declared block size out of payload range");
            var blockEnd = rowsStart + (int)(declaredSize - 4);
            if (blockEnd < rowsStart || blockEnd > data.Length)
                throw new ParseInlineException("declared block size out of payload range");
            return new BlockFrame { DeclaredSize = declaredSize, RowCount = rowCount, RowsStart = rowsStart, BlockEnd = blockEnd };
        }



        private static List<RowRec> ParseMonopolyBlock(byte[] data, int markerPos, byte[] marker)
        {
            ParseEnvelope(true, data, markerPos);                                // 常量+segment 校验
            var frame = ParseBlockFrame(data, markerPos, marker);
            return ParseMonopolyRows(data, frame.RowsStart, frame.BlockEnd, frame.RowCount);
        }

        private sealed class RowCore
        {
            public uint RawRollPoints; public string ItemSpec = "";
            public uint SecondaryCount; public string SecondaryItem = "";
            public string? PoolId; public ulong Ticks;
        }

        private sealed class RowRec
        {
            public string RecordType = ""; public ulong Ticks; public DateTime Time;
            public string? PoolId; public string? LotteryId;
            public string ItemId = ""; public uint Count;
            public uint? RollPoints; public string? RollLabelId;
            public string? SecondaryItemId; public uint? SecondaryCount;
        }

        private static List<RowRec> ParseMonopolyRows(byte[] data, int rowsStart, int blockEnd, uint rowCount)
        {
            var states = new SortedDictionary<int, List<List<RowRec>>>
            {
                [rowsStart] = new List<List<RowRec>> { new List<RowRec>() }
            };

            for (uint index = 0; index < rowCount; index++)
            {
                var next = new SortedDictionary<int, List<List<RowRec>>>();
                foreach (var kv in states)
                {
                    if (!TryParseMonopolyRowCore(data, kv.Key, blockEnd, out var core, out var coreEnd)) continue;

                    var tails = new List<(int Pos, string? LotteryId)> { (coreEnd, null) };
                    var tailReader = new Reader(data, coreEnd, blockEnd);
                    if (tailReader.TryString(out var lid) && GachaProtocolTypes.IsValidLotteryId(lid))
                        tails.Add((tailReader.Pos, lid));

                    foreach (var (nextPos, lotteryId) in tails)
                    {
                        foreach (var path in kv.Value)
                        {
                            var rows = new List<RowRec>(path) { BuildMonopolyRow(core, lotteryId, index) };
                            if (!next.TryGetValue(nextPos, out var candidates))
                            {
                                candidates = new List<List<RowRec>>();
                                next[nextPos] = candidates;
                            }
                            // [P4] 候选路径上限 2 → 8,防止真路径被截断挤掉
                            if (candidates.Count < 8) candidates.Add(rows);
                        }
                    }
                }
                states = next;
                if (states.Count == 0) break;
            }

            // [P3] 多解不再一律丢弃:内容等价的多条切分路径视为同一解;只有真正冲突才报错
            if (!states.TryGetValue(blockEnd, out var solutions) || solutions.Count == 0)
                throw new ParseInlineException("monopoly block has 0 valid layouts");
            if (solutions.Count > 1)
                for (var i = 1; i < solutions.Count; i++)
                    if (!SameRecords(solutions[0], solutions[i]))
                        throw new ParseInlineException($"monopoly block has {solutions.Count} conflicting layouts");
            return solutions[0];
        }

        /// <summary>[P3] 两条切分路径产出的记录序列完全一致 → 视为同一解,不算歧义。</summary>
        private static bool SameRecords(List<RowRec> a, List<RowRec> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
            {
                var (x, y) = (a[i], b[i]);
                if (x.Ticks != y.Ticks || x.ItemId != y.ItemId || x.Count != y.Count ||
                    x.PoolId != y.PoolId || x.LotteryId != y.LotteryId ||
                    x.RollPoints != y.RollPoints || x.RollLabelId != y.RollLabelId ||
                    x.SecondaryItemId != y.SecondaryItemId || x.SecondaryCount != y.SecondaryCount)
                    return false;
            }
            return true;
        }


        private static bool TryParseMonopolyRowCore(byte[] data, int pos, int blockEnd, out RowCore core, out int coreEnd)
        {
            try
            {
                var reader = new Reader(data, pos, blockEnd);
                var rawRollPoints = reader.U32();
                var itemSpec = reader.String();
                // [P1] 该字段在所有真实记录中恒为 0:硬校验,错位路径读到这里几乎必为非 0,立即淘汰
                var zero = reader.U32();
                if (zero != 0) throw new ParseInlineException($"row zero field non-zero: {zero}");
                var secondaryCount = reader.U32();
                var secondaryItem = reader.String();
                var resultOrPool = reader.String();
                var poolStart = reader.Pos;
                string? poolId;
                if (reader.TryString(out var pool) && pool.StartsWith("CardPool_", StringComparison.Ordinal))
                    poolId = pool;
                else
                {
                    reader.Pos = poolStart;
                    poolId = resultOrPool.StartsWith("CardPool_", StringComparison.Ordinal) ? resultOrPool : null;
                }
                // [P2] 时间戳硬校验:错位路径读到的随机 8 字节几乎必落在有效时间(2017~2100)之外
                var ticks = reader.U64();
                if (TicksToDateTime(ticks) == DateTime.MinValue)
                    throw new ParseInlineException($"row ticks invalid: {ticks}");
                core = new RowCore
                {
                    RawRollPoints = rawRollPoints,
                    ItemSpec = itemSpec,
                    SecondaryCount = secondaryCount,
                    SecondaryItem = secondaryItem,
                    PoolId = poolId,
                    Ticks = ticks,
                };
                coreEnd = reader.Pos;
                return true;
            }
            catch (ParseInlineException) { core = null!; coreEnd = 0; return false; }
        }

        private static RowRec BuildMonopolyRow(RowCore core, string? lotteryId, uint index)
        {
            var (itemId, count) = ParseItemSpec(core.ItemSpec);
            return new RowRec
            {
                RecordType = "monopoly",
                Ticks = core.Ticks,
                Time = TicksToDateTime(core.Ticks),
                PoolId = core.PoolId,
                LotteryId = lotteryId,
                ItemId = itemId,
                Count = count,
                RollPoints = GachaProtocolTypes.IsRollPointSentinel(core.RawRollPoints) ? null : core.RawRollPoints,
                RollLabelId = GachaProtocolTypes.RollLabelFromSentinel(core.RawRollPoints),
                SecondaryItemId = core.SecondaryItem.Length > 0 ? core.SecondaryItem : null,
                SecondaryCount = core.SecondaryCount,
            };
        }


        private static List<RowRec> ParseForkBlock(byte[] data, int markerPos, byte[] marker)
        {
            ParseEnvelope(false, data, markerPos);
            var frame = ParseBlockFrame(data, markerPos, marker);
            var reader = new Reader(data, frame.RowsStart, frame.BlockEnd);
            var rows = new List<RowRec>();
            for (uint index = 0; index < frame.RowCount; index++)
            {
                var itemSpec = reader.String();
                var poolId = reader.String();
                var ticks = reader.U64();
                var (itemId, count) = ParseItemSpec(itemSpec);
                rows.Add(new RowRec
                {
                    RecordType = "fork",
                    Ticks = ticks,
                    Time = TicksToDateTime(ticks),
                    PoolId = poolId,
                    LotteryId = null,
                    ItemId = itemId,
                    Count = count,
                    RollPoints = null,
                    RollLabelId = null,
                    SecondaryItemId = null,
                    SecondaryCount = null,
                });
            }
            if (reader.Pos != frame.BlockEnd)
                throw new ParseInlineException("fork rows do not consume declared block size");
            return rows;
        }


        private sealed class Reader
        {
            public Reader(byte[] data, int pos, int end) { Data = data; Pos = pos; End = end; }
            public readonly byte[] Data; public int Pos; public readonly int End;

            public uint U32()
            {
                if (Pos + 4 > End || Pos + 4 > Data.Length) throw new ParseInlineException("u32 out of block range");
                var v = U32At(Data, Pos); Pos += 4; return v;
            }

            public ulong U64()
            {
                if (Pos + 8 > End || Pos + 8 > Data.Length) throw new ParseInlineException("u64 out of block range");
                var v = U64At(Data, Pos); Pos += 8; return v;
            }

            public string String()
            {
                var lenPos = Pos;
                var length = (int)U32();
                if (length == 0 || length > 256) throw new ParseInlineException($"invalid string length {length} at {lenPos}");
                var end = Pos + length;
                if (end > End || end > Data.Length) throw new ParseInlineException("string out of payload range");
                var rawEnd = Data[end - 1] == 0 ? end - 1 : end;
                var s = Encoding.UTF8.GetString(Data, Pos, rawEnd - Pos);
                Pos = end;
                return s;
            }

            public bool TryString(out string value)
            {
                var start = Pos;
                try { value = String(); return true; }
                catch (ParseInlineException) { Pos = start; value = ""; return false; }
            }
        }

        private static byte[] DecodeShiftedBytes(byte[] data, int byteOff, int bitShift, int count)
        {
            var output = new List<byte>(count);
            for (var i = 0; i < count; i++)
            {
                var bitPos = (byteOff + i) * 8 + bitShift;
                var bOff = bitPos / 8; var bShift = bitPos % 8;
                if (bOff >= data.Length) break;
                var value = data[bOff] >> bShift;
                if (bShift != 0 && bOff + 1 < data.Length) value |= data[bOff + 1] << (8 - bShift);
                output.Add((byte)(value & 0xFF));
            }
            return output.ToArray();
        }

        private static bool ShiftedViewContainsMarker(byte[] data, int byteOff, int bitShift, int count)
            => ShiftedViewContainsBytes(data, byteOff, bitShift, count, GachaProtocolTypes.MonopolyMarkerBytes)
            || ShiftedViewContainsBytes(data, byteOff, bitShift, count, GachaProtocolTypes.ForkMarkerBytes);

        private static bool ShiftedViewContainsBytes(byte[] data, int byteOff, int bitShift, int count, byte[] needle)
        {
            if (needle.Length == 0 || count < needle.Length) return false;
            for (var start = 0; start <= count - needle.Length; start++)
            {
                var ok = true;
                for (var i = 0; i < needle.Length; i++)
                {
                    if (ShiftedByte(data, byteOff, bitShift, start + i) != needle[i]) { ok = false; break; }
                }
                if (ok) return true;
            }
            return false;
        }

        private static int ShiftedByte(byte[] data, int byteOff, int bitShift, int index)
        {
            var bitPos = (byteOff + index) * 8 + bitShift;
            var bOff = bitPos / 8; var bShift = bitPos % 8;
            if (bOff >= data.Length) return -1;
            var value = data[bOff] >> bShift;
            if (bShift != 0 && bOff + 1 < data.Length) value |= data[bOff + 1] << (8 - bShift);
            return value & 0xFF;
        }

        private static int FindBytes(byte[] data, int start, byte[] needle)
        {
            for (var i = start; i <= data.Length - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                    if (data[i + j] != needle[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }


        private static uint U32At(byte[] d, int pos)
        {
            if (pos < 0 || pos + 4 > d.Length) throw new ParseInlineException("u32 out of range");
            return (uint)d[pos]
                 | (uint)d[pos + 1] << 8
                 | (uint)d[pos + 2] << 16
                 | (uint)d[pos + 3] << 24;
        }


        private static ulong U64At(byte[] d, int pos)
        {
            if (pos < 0 || pos + 8 > d.Length) throw new ParseInlineException("u64 out of range");
            return (ulong)d[pos]
                 | (ulong)d[pos + 1] << 8
                 | (ulong)d[pos + 2] << 16
                 | (ulong)d[pos + 3] << 24
                 | (ulong)d[pos + 4] << 32
                 | (ulong)d[pos + 5] << 40
                 | (ulong)d[pos + 6] << 48
                 | (ulong)d[pos + 7] << 56;
        }

        private static uint RelativeU32(byte[] data, int markerPos, int rel)
        {
            var pos = markerPos + rel;
            if (pos < 0) throw new ParseInlineException("protocol envelope out of range");
            return U32At(data, pos);
        }

        private static (string ItemId, uint Count) ParseItemSpec(string itemSpec)
        {
            var idx = itemSpec.LastIndexOf(',');
            if (idx < 0) return (itemSpec, 1);
            if (uint.TryParse(itemSpec[(idx + 1)..], out var amount) && amount > 0)
                return (itemSpec[..idx], amount);
            return (itemSpec, 1);
        }


        private static DateTime TicksToDateTime(ulong ticks)
        {
            var delta = (long)ticks - DotnetEpochTicks;
            if (delta < 0) return DateTime.MinValue;
            var seconds = delta / TicksPerSecond;
            if (seconds < 1_500_000_000 || seconds > 4_102_444_800) return DateTime.MinValue;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(delta);
        }

        /// 名称/星级/池名走</summary>
        private static GachaRecord BuildRecord(RowRec r)
        {
            var material = string.Join('\x1f',
                r.RecordType, r.Ticks.ToString(),
                r.PoolId ?? "", r.LotteryId ?? "", r.ItemId, r.Count.ToString(),
                r.RollPoints?.ToString() ?? "", r.RollLabelId ?? "",
                r.SecondaryItemId ?? "", r.SecondaryCount?.ToString() ?? "");
            var poolLabel = GachaMapCatalog.BannerTitle(r.LotteryId, r.PoolId)
                         ?? GachaMapCatalog.PoolLabel(r.PoolId);
            return new GachaRecord
            {
                Time = r.Time,
                RecordType = r.RecordType,
                RecordId = Sha256Hex(material),
                PoolId = r.PoolId ?? "",
                PoolName = string.IsNullOrEmpty(poolLabel) ? r.PoolId ?? "" : poolLabel,
                LotteryId = r.LotteryId ?? "",
                ItemId = r.ItemId,
                ItemName = GachaMapCatalog.ItemName(r.ItemId),
                Rarity = GachaMapCatalog.ItemRarity(r.ItemId),
                Count = (int)r.Count,
            };
        }


        private static string Sha256Hex(string material)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // ════════ 异常 ════════

        private sealed class ParseInlineException : Exception
        {
            public ParseInlineException(string message) : base(message) { }
        }

        private sealed class PendingOriginalException : Exception
        {
            public string FunctionName { get; }
            public PendingOriginalException(string functionName) : base($"待原文: {functionName}") { FunctionName = functionName; }
        }
    }
}
