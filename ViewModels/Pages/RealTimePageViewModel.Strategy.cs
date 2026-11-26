using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TrustNTE.Models;
using TrustNTE.Services;

namespace TrustNTE.ViewModels.Pages
{
    public partial class RealTimePageViewModel
    {
        // ═══════ 战斗策略系统（P1）属性 ═══════
        public ObservableCollection<string> StrategyNames { get; } = new();

        [ObservableProperty] private string? _selectedStrategyName;
        [ObservableProperty] private string _strategyName = "";
        [ObservableProperty] private string _strategyStatus = "";
        [ObservableProperty] private string? _teamSlot1;
        [ObservableProperty] private string? _teamSlot2;
        [ObservableProperty] private string? _teamSlot3;
        [ObservableProperty] private string? _teamSlot4;
        [ObservableProperty] private string _scriptSlot1 = "";
        [ObservableProperty] private string _scriptSlot2 = "";
        [ObservableProperty] private string _scriptSlot3 = "";
        [ObservableProperty] private string _scriptSlot4 = "";

        // ═══════ 战斗策略系统（P1）方法 ═══════
        partial void OnSelectedStrategyNameChanged(string? value)
        {
            if (!string.IsNullOrEmpty(value)) LoadIntoEditor(StrategyService.Load(value));
        }

        [RelayCommand]
        private void NewStrategy()
        {
            SelectedStrategyName = null;
            StrategyName = "新策略";
            TeamSlot1 = TeamSlot2 = TeamSlot3 = TeamSlot4 = null;
            ScriptSlot1 = ScriptSlot2 = ScriptSlot3 = ScriptSlot4 = "";
            StrategyStatus = "新建空白策略 → 配队伍 → 保存";
        }

        [RelayCommand]
        private void SaveStrategy()
        {
            var doc = BuildDocFromEditor();
            if (doc == null) return;
            StrategyService.Save(doc);
            if (!StrategyNames.Contains(doc.Info.Name)) StrategyNames.Add(doc.Info.Name);
            SelectedStrategyName = doc.Info.Name;
            Config.AutoCombatStrategyName = doc.Info.Name;
            StrategyStatus = $"✅ 已保存 [{doc.Info.Name}] {DateTime.Now:HH:mm:ss}";
        }

        [RelayCommand]
        private void DeleteStrategy()
        {
            if (SelectedStrategyName == null) { StrategyStatus = "先选择要删除的策略"; return; }
            StrategyService.Delete(SelectedStrategyName);
            StrategyNames.Remove(SelectedStrategyName);
            //    删除的是当前生效策略 → 清空绑定，热载直接回默认（否则引擎走"加载失败"弯路）
            if (Config.AutoCombatStrategyName == SelectedStrategyName)
                Config.AutoCombatStrategyName = "";
            SelectedStrategyName = StrategyNames.FirstOrDefault();
            StrategyStatus = "已删除";
        }

        [RelayCommand]
        private void ImportStrategy()
        {
            var path = _fileDialogService.PickOpenFile("策略文件 (*.json)|*.json", StrategyService.Dir);
            if (path == null) return;

            var (doc, warnings) = StrategyService.ImportFrom(path);
            if (doc == null)
            {
                StrategyStatus = $"❌ 导入失败: {warnings.FirstOrDefault()}";
                return;
            }
            StrategyService.Save(doc);
            if (!StrategyNames.Contains(doc.Info.Name)) StrategyNames.Add(doc.Info.Name);
            SelectedStrategyName = doc.Info.Name;
            LoadIntoEditor(doc);
            StrategyStatus = "✅ 导入成功" + (warnings.Count > 0 ? "（警告: " + string.Join("; ", warnings) + "）" : "");
        }

        [RelayCommand]
        private void ExportStrategy()
        {
            var doc = BuildDocFromEditor();
            if (doc == null) return;
            var path = _fileDialogService.PickSaveFile("策略文件 (*.json)|*.json", doc.Info.Name + ".json", StrategyService.Dir);
            if (path == null) return;
            StrategyService.ExportTo(doc, path);
            StrategyStatus = $"✅ 已导出: {path}";
        }

        public void HandleSlotCommitted(int slot, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (slot != 1 && TeamSlot1 == value) TeamSlot1 = null;
            if (slot != 2 && TeamSlot2 == value) TeamSlot2 = null;
            if (slot != 3 && TeamSlot3 == value) TeamSlot3 = null;
            if (slot != 4 && TeamSlot4 == value) TeamSlot4 = null;
        }

        private StrategyDocument? BuildDocFromEditor()
        {
            var name = StrategyName.Trim();
            if (string.IsNullOrEmpty(name))
            {
                StrategyStatus = "请先填写策略名";
                return null;
            }

            string?[] slots = { TeamSlot1, TeamSlot2, TeamSlot3, TeamSlot4 };
            string[] scripts = { ScriptSlot1, ScriptSlot2, ScriptSlot3, ScriptSlot4 };
            var actions = new List<StrategyAction>();
            for (int i = 0; i < 4; i++)
                if (!string.IsNullOrWhiteSpace(slots[i]))
                    actions.Add(new StrategyAction { Name = $"{slots[i]}-seq", Character = slots[i]!, Action = scripts[i] });

            return new StrategyDocument
            {
                Info = new StrategyInfo { Name = name, Author = "本地" },
                Team = slots.ToList(),
                Actions = actions
            };
        }

        private void LoadIntoEditor(StrategyDocument? doc)
        {
            if (doc == null) { StrategyStatus = "策略加载失败"; return; }
            StrategyName = doc.Info.Name;
            var t = doc.Team;
            TeamSlot1 = t.Count > 0 ? t[0] : null;
            TeamSlot2 = t.Count > 1 ? t[1] : null;
            TeamSlot3 = t.Count > 2 ? t[2] : null;
            TeamSlot4 = t.Count > 3 ? t[3] : null;
            ScriptSlot1 = FindScript(doc, TeamSlot1);
            ScriptSlot2 = FindScript(doc, TeamSlot2);
            ScriptSlot3 = FindScript(doc, TeamSlot3);
            ScriptSlot4 = FindScript(doc, TeamSlot4);
        }

        private static string FindScript(StrategyDocument doc, string? ch)
            => string.IsNullOrEmpty(ch) ? "" : doc.Actions.FirstOrDefault(a => a.Character == ch)?.Action ?? "";
    }
}
