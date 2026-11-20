using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using TrustNTE.Models.Dps;

namespace TrustNTE.ViewModels.Pages
{
    /// <summary>DPS VM：Core 事件、真实 DTO 映射、时间线和逐击查询。</summary>
    public partial class DpsAnalysisPageViewModel
    {
        private const string GameProcessName = "HTGame";
        private const int MaxTimelineBars = 176;
        private const double TimelineFullHeight = 140;
        private const int AxisPageLimit = 500;

        private void OnBattleSummaryReceived(DpsBattleSummaryDto summary)
            => DispatchToUi(() => ApplySummary(summary));

        private void OnCaptureStatusReceived(DpsCaptureStatusDto status)
        {
            DispatchToUi(() =>
            {
                IsCapturing = status.Status is "starting" or "running";
                CaptureStatusText = status.Status switch
                {
                    "starting" => "Core 正在启动抓包线程…",
                    "running" => "抓包运行中，等待有效战斗数据。",
                    "stopped" => "Core 已排空采集事件，最终战报可查询。",
                    _ => $"Core 抓包状态：{status.Status}。"
                };
                if (status.Status == "stopped")
                {
                    _timelineTimer.Stop();
                    RefreshTimelineCommand.Execute(null);
                }
            });
        }

        private void OnCoreStateChanged(bool isRunning)
        {
            DispatchToUi(() =>
            {
                CoreLampOn = isRunning;
                if (!isRunning)
                {
                    IsCapturing = false;
                    _timelineTimer.Stop();
                }
            });
        }

        private void OnCoreDiagnosticReceived(string level, string message)
        {
            DispatchToUi(() =>
            {
                CaptureStatusText = level == "error"
                    ? $"Core 错误：{message}"
                    : $"Core 警告：{message}";
            });
        }

        private void DispatchToUi(Action action)
        {
            var session = _windowCts;
            _dispatcherService.BeginInvoke(() =>
            {
                if (_disposed
                    || session.IsCancellationRequested
                    || !ReferenceEquals(session, _windowCts)) return;
                action();
            });
        }

        private void ProbeGameProcess()
        {
            if (_disposed) return;
            var processes = Process.GetProcessesByName(GameProcessName);
            try
            {
                GameLampOn = processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }

        private void ApplySummary(DpsBattleSummaryDto summary)
        {
            TotalDamageStr = F(summary.TotalDamage);
            TeamDpsStr = F(summary.TotalDps);
            DurationStr = Ts(summary.DurationSeconds);
            ElapsedBadge = DurationStr;
            DamageTakenStr = F(summary.TotalDamageTaken);
            HitsStr = summary.TotalHits.ToString("N0");
            MaxHpReductionStr = F(summary.MaxHpReduction);

            UpdateCharacters(summary.Characters);
            UpdateSkills(summary.Skills);
            UpdateQuality(summary.Quality);
            UpdateAbyss(summary.Abyss);
            UpdateDonut();
        }

        private void UpdateCharacters(List<DpsBattleSummaryDto.CharacterInfo> source)
        {
            var ordered = source.OrderByDescending(character => character.Damage).ToList();
            var retainedIds = ordered.Select(character => character.CharId).ToHashSet();

            for (var index = Characters.Count - 1; index >= 0; index--)
            {
                if (!retainedIds.Contains(Characters[index].CharId))
                    Characters.RemoveAt(index);
            }

            for (var index = 0; index < ordered.Count; index++)
            {
                var dto = ordered[index];
                var row = Characters.FirstOrDefault(character => character.CharId == dto.CharId);
                if (row == null)
                {
                    row = new CharacterRowVm(dto.CharId, dto.Name, ResolveCharacterAttribute(dto.Name));
                    Characters.Insert(Math.Min(index, Characters.Count), row);
                }
                else
                {
                    var oldIndex = Characters.IndexOf(row);
                    if (oldIndex != index) Characters.Move(oldIndex, index);
                }

                row.Num = dto.Damage;
                row.DamageStr = F(dto.Damage);
                row.DpsStr = "DPS " + F(dto.Dps);
                row.ShareStr = dto.DamageSharePercent.ToString("0.0") + "%";
                row.BarWidth = dto.DamageSharePercent * 2.4;
            }
        }

        private void UpdateSkills(List<DpsBattleSummaryDto.SkillInfo> source)
        {
            var ordered = source.OrderByDescending(skill => skill.Damage).ToList();
            var retainedKeys = ordered.Select(SkillKey).ToHashSet();

            for (var index = Skills.Count - 1; index >= 0; index--)
            {
                if (!retainedKeys.Contains(Skills[index].Key)) Skills.RemoveAt(index);
            }

            for (var index = 0; index < ordered.Count; index++)
            {
                var dto = ordered[index];
                var key = SkillKey(dto);
                var row = Skills.FirstOrDefault(skill => skill.Key == key);
                if (row == null)
                {
                    var character = Characters.FirstOrDefault(item => item.CharId == dto.CharId);
                    var attribute = character?.Attribute ?? ResolveCharacterAttribute(dto.CharName);
                    var displayName = string.IsNullOrWhiteSpace(dto.Category)
                        ? dto.Name
                        : $"{dto.Name} [{dto.Category}]";
                    row = new SkillRowVm(
                        key, dto.CharId, dto.CharName, displayName, attribute, dto.IsFollowUp);
                    Skills.Insert(Math.Min(index, Skills.Count), row);
                }
                else
                {
                    var oldIndex = Skills.IndexOf(row);
                    if (oldIndex != index) Skills.Move(oldIndex, index);
                }

                row.Num = dto.Damage;
                row.DamageStr = F(dto.Damage);
                row.PctStr = dto.DamageSharePercent.ToString("0.0") + "%";
                row.BarWidth = dto.DamageSharePercent * 1.3;
            }
        }

        private void UpdateQuality(DpsBattleSummaryDto.QualityInfo quality)
        {
            QualityItems.Clear();
            AddQuality("SOURCE", quality.Source, false);
            AddQuality("包", quality.PacketCount.ToString("N0"), false);
            AddQuality("命中包", quality.PacketsWithHits.ToString("N0"), false);
            AddQuality("解析命中", quality.HitCount.ToString("N0"), false);
            AddQuality("输出命中", quality.OutgoingHits.ToString("N0"), false);
            AddQuality("输出伤害", F(quality.OutgoingDamage), false);
            AddQuality("未知方向命中", quality.UnknownDirectionHits.ToString("N0"), quality.UnknownDirectionHits > 0);
            AddQuality("未知方向伤害", F(quality.UnknownDirectionDamage), quality.UnknownDirectionDamage > 0);
            AddQuality("受击命中", quality.IncomingHits.ToString("N0"), false);
            AddQuality("受击伤害", F(quality.IncomingDamage), false);
            AddQuality("未知角色数", quality.UnknownCharacterCount.ToString("N0"), quality.UnknownCharacterCount > 0);
            AddQuality("未知角色命中", quality.UnknownCharacterHits.ToString("N0"), quality.UnknownCharacterHits > 0);
            AddQuality("未映射技能行", quality.UnmappedSkillRows.ToString("N0"), quality.UnmappedSkillRows > 0);
            AddQuality("未映射技能命中", quality.UnmappedSkillHits.ToString("N0"), quality.UnmappedSkillHits > 0);
            AddQuality("未映射 GE 数", quality.UnmappedGameplayEffectCount.ToString("N0"), quality.UnmappedGameplayEffectCount > 0);
            AddQuality("时停事件", quality.TimeStopEventCount.ToString("N0"), false);
            AddQuality("时停区间", quality.TimeStopIntervalCount.ToString("N0"), false);
            AddQuality("深渊事件", quality.AbyssEventCount.ToString("N0"), false);
            AddQuality("服务器修正", quality.ServerDamageCorrections.ToString("N0"), false);
            AddQuality("未归因服务器事件", quality.UnattributedServerDamageEvents.ToString("N0"), quality.UnattributedServerDamageEvents > 0);
            AddQuality("未归因服务器伤害", F(quality.UnattributedServerDamage), quality.UnattributedServerDamage > 0);
        }

        private void AddQuality(string label, string value, bool warning)
            => QualityItems.Add(new QualityChipVm(label, value, warning));

        private void UpdateAbyss(DpsBattleSummaryDto.AbyssInfo abyss)
        {
            AbyssLampOn = abyss.Detected;
            if (!abyss.Detected)
            {
                AbyssFloorText = "--F";
                AbyssStateText = "ABYSS · 未检测";
                UpperDur = "00:00";
                UpperDmg = "0";
                UpperSub = "等待上半数据…";
                LowerDur = "00:00";
                LowerDmg = "0";
                LowerSub = "等待下半数据…";
                return;
            }

            AbyssFloorText = abyss.Floor.HasValue ? $"{abyss.Floor.Value}F" : "--F";
            var halfText = abyss.ActiveHalf switch
            {
                "upper" => "上半进行中",
                "lower" => "下半进行中",
                _ => "等待换半"
            };
            AbyssStateText = abyss.Success ? "ABYSS · 挑战成功" : $"ABYSS · {halfText}";
            ApplyAbyssHalf(abyss.FirstHalf, true);
            ApplyAbyssHalf(abyss.SecondHalf, false);
        }

        private void ApplyAbyssHalf(DpsBattleSummaryDto.AbyssHalfInfo? half, bool upper)
        {
            var duration = half == null ? "00:00" : Ts(half.DurationSeconds);
            var damage = half == null ? "0" : F(half.TotalDamage);
            var subtitle = half == null
                ? (upper ? "等待上半数据…" : "等待下半数据…")
                : BuildHalfSubtitle(half);

            if (upper)
            {
                UpperDur = duration;
                UpperDmg = damage;
                UpperSub = subtitle;
            }
            else
            {
                LowerDur = duration;
                LowerDmg = damage;
                LowerSub = subtitle;
            }
        }

        private static string BuildHalfSubtitle(DpsBattleSummaryDto.AbyssHalfInfo half)
        {
            var leaders = string.Join(" / ", half.Characters
                .OrderByDescending(character => character.Damage)
                .Take(3)
                .Select(character => character.Name));
            return $"DPS {F(half.TotalDps)}" + (leaders.Length > 0 ? $" · {leaders}" : "");
        }

        [RelayCommand]
        private async Task RefreshTimelineAsync()
        {
            if (_disposed || _windowCts.IsCancellationRequested || !_dpsCore.IsCoreRunning) return;
            try
            {
                var timeline = await _dpsCore.GetTimelineAsync(_windowCts.Token);
                if (timeline != null) ApplyTimeline(timeline);
            }
            catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
            {
                // 关窗取消。
            }
            catch (DpsCoreException ex) when (ex.DomainCode == "BATTLE_RECORD_NOT_FOUND")
            {
                _logService.LogDebug("[DPS] 时间线对应的旧战报已失效，等待下一条摘要。");
            }
            catch (Exception ex)
            {
                _logService.LogDebug($"[DPS] 时间线刷新失败：{ex.Message}");
            }
        }

        private void ApplyTimeline(DpsBattleTimelineDto timeline)
        {
            TimelineBars.Clear();
            if (timeline.Buckets.Count == 0)
            {
                UpdateLinePoints();
                return;
            }

            var firstVisibleIndex = Math.Max(0, timeline.Buckets.Count - MaxTimelineBars);
            var lastBucketIndex = timeline.Buckets.Count - 1;
            var peak = Math.Max(timeline.PeakDps, 1);

            for (var index = firstVisibleIndex; index < timeline.Buckets.Count; index++)
            {
                var bucket = timeline.Buckets[index];
                var isMarker = timeline.Markers.Any(marker =>
                    marker.OffsetSeconds >= bucket.StartOffsetSeconds
                    && (marker.OffsetSeconds < bucket.EndOffsetSeconds
                        || (index == lastBucketIndex && marker.OffsetSeconds <= bucket.EndOffsetSeconds)));
                var isTimeStop = timeline.TimeStopIntervals.Any(interval =>
                    interval.StartOffsetSeconds < bucket.EndOffsetSeconds
                    && interval.EndOffsetSeconds > bucket.StartOffsetSeconds);
                var kind = isMarker ? "marker" : isTimeStop ? "tstop" : "dps";

                TimelineBars.Add(new BucketBarVm
                {
                    Kind = kind,
                    Num = bucket.TeamDps,
                    HeightPct = kind == "dps"
                        ? Math.Max(3, bucket.TeamDps / peak * TimelineFullHeight)
                        : TimelineFullHeight
                });
            }
            UpdateLinePoints();
        }

        private async Task LoadCharacterAxisAsync(uint characterId, CancellationToken cancellationToken)
        {
            string? cursor = null;
            var visitedCursors = new HashSet<string>(StringComparer.Ordinal);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _dpsCore.GetAxisPageAsync(cursor, AxisPageLimit, cancellationToken);
                if (page == null) break;

                foreach (var hit in page.Rows.Where(hit =>
                             hit.CharacterId == characterId && hit.Direction == "outgoing"))
                {
                    AxisRows.Add(new AxisRowVm
                    {
                        TimeStr = hit.RelativeTimeSeconds.ToString("0.00") + "s",
                        TypeStr = FirstNonBlank(
                            hit.AbilityName,
                            hit.DamageName,
                            hit.GameplayEffectName,
                            hit.AttackType) ?? "未映射技能",
                        DamageStr = F(hit.TotalDamage),
                        TargetStr = ResolveTargetDisplay(hit.TargetName, hit.TargetId)


                    });
                }

                cursor = page.NextCursor;
                if (string.IsNullOrWhiteSpace(cursor)) break;
                if (!visitedCursors.Add(cursor))
                    throw new InvalidDataException("Core 逐击分页 next_cursor 出现循环。");
            }
        }

        private static string ResolveTargetDisplay(string? targetName, string? targetId)
        {
            if (!string.IsNullOrWhiteSpace(targetName)) return targetName;
            if (string.IsNullOrWhiteSpace(targetId)) return "未知目标";
            return targetId.StartsWith("wire:", StringComparison.Ordinal) ? "未知目标" : targetId;
        }
        private void ResetBattle()
        {
            Characters.Clear();
            Skills.Clear();
            TimelineBars.Clear();
            QualityItems.Clear();
            DetailSkills.Clear();
            AxisRows.Clear();

            TotalDamageStr = "0";
            TeamDpsStr = "0";
            DurationStr = "00:00";
            ElapsedBadge = "00:00";
            DamageTakenStr = "0";
            HitsStr = "0";
            MaxHpReductionStr = "0";
            UpperDur = "00:00";
            UpperDmg = "0";
            UpperSub = "等待上半数据…";
            LowerDur = "00:00";
            LowerDmg = "0";
            LowerSub = "等待下半数据…";
            AbyssFloorText = "--F";
            AbyssStateText = "ABYSS · 未检测";
            AbyssLampOn = false;
            LinePoints = new();
            AreaPoints = new();
            UpdateDonut();
        }

        private string ResolveCharacterAttribute(string name)
        {
            if (_characterCatalogFailed) return "未知";
            try
            {
                var record = TrustNTE.Helpers.CharacterCatalog.All.FirstOrDefault(
                    character => string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase));
                return string.IsNullOrWhiteSpace(record?.Element) ? "未知" : record.Element;
            }
            catch (Exception ex)
            {
                _characterCatalogFailed = true;
                _logService.LogWarning($"[DPS] 角色名册不可用，属性统一显示“未知”：{ex.Message}");
                return "未知";
            }
        }

        private void HandleCommandException(Exception exception, string action)
        {
            if (exception is OperationCanceledException && _windowCts.IsCancellationRequested)
            {
                CaptureStatusText = "操作已取消。";
                return;
            }

            CaptureStatusText = FriendlyError(exception, action);
            _logService.LogError($"[DPS] {action}失败：{exception.Message}");
        }

        private static string FriendlyError(Exception exception, string action)
        {
            if (exception is DpsCoreException coreException)
            {
                return coreException.DomainCode switch
                {
                    "NPCAP_NOT_FOUND" => "未检测到 Npcap；请安装 Npcap，并建议启用 WinPcap API-compatible Mode。",
                    "GAME_PROCESS_NOT_FOUND" => "未检测到游戏进程或活动连接；请启动游戏后重试，或改用手动网卡。",
                    "CAPTURE_DEVICE_NOT_FOUND" => "所选 Npcap 网卡已不可用，请返回并刷新网卡列表。",
                    "CAPTURE_ALREADY_RUNNING" => "Core 中已有抓包任务；请先停止或关闭旧会话。",
                    "CAPTURE_NOT_RUNNING" => "Core 当前没有运行中的抓包任务。",
                    "PROTOCOL_VERSION_MISMATCH" => "Nte-Dps.exe 协议版本不兼容，请更换配套版本。",
                    "CAPABILITY_NOT_SUPPORTED" => "Nte-Dps.exe 缺少战报能力，请更换配套版本。",
                    "HANDSHAKE_REQUIRED" => "Core 握手状态失效，请返回后重新启动。",
                    _ => $"{action}失败（{coreException.DomainCode}）：{coreException.Message}"
                };
            }

            return exception switch
            {
                FileNotFoundException => exception.Message,
                TimeoutException => $"{action}超时：Nte-Dps.exe 未在限定时间内响应。",
                InvalidDataException => $"{action}失败：Core 返回的数据无法识别。{exception.Message}",
                _ => $"{action}失败：{exception.Message}"
            };
        }

        private static string SkillKey(DpsBattleSummaryDto.SkillInfo skill)
            => $"{skill.CharId}|{skill.Name}|{skill.Category}|{skill.AbilityName}|{skill.GameplayEffectName}|{skill.IsFollowUp}";

        private static string? FirstNonBlank(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static string F(double value) => Math.Round(value).ToString("N0");
        private static string Ts(double seconds)
            => $"{(int)(seconds / 60):D2}:{(int)(seconds % 60):D2}";
    }
}
