using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrustNTE.Models;
using TrustNTE.Services;
using TrustNTE.Services.Interfaces;
using TrustNTE.Views.Windows;

namespace TrustNTE.ViewModels.Pages
{
    /// <summary>任务行显示包装：POCO 模型 + 运行状态列。</summary>
    public partial class TaskRow : ObservableObject
    {
        public SchedulerTaskItem Item { get; }
        public string Name => Item.Name;
        public string Type => Item.Type;
        public int RunNum => Item.RunNum;
        [ObservableProperty] private string _status = "待命";
        public TaskRow(SchedulerTaskItem item) { Item = item; }
    }

    public partial class SchedulerViewModel : ObservableObject
    {
        private readonly SchedulerService _runner;
        private readonly ILogService _log;

        public ObservableCollection<SchedulerGroupDoc> Groups { get; } = new();
        public ObservableCollection<TaskRow> TaskRows { get; } = new();
        public ObservableCollection<string> AvailableScripts { get; } = new();

        [ObservableProperty] private SchedulerGroupDoc? _selectedGroup;
        [ObservableProperty] private string? _pickedScript;
        [ObservableProperty] private bool _isRunning;
        [ObservableProperty] private string _runStatusText = "空闲";

        public SchedulerViewModel(SchedulerService runner, ILogService log)
        {
            _runner = runner;
            _log = log;
            _runner.OnRunningChanged += running =>
            { IsRunning = running; RunStatusText = running ? "执行中…" : "空闲"; };
            _runner.OnTaskStateChanged += (_, task, status) =>
            {
                var row = TaskRows.FirstOrDefault(r => r.Name == task);
                if (row != null) row.Status = status;
            };
            Reload();
            RefreshScripts();
        }

        partial void OnSelectedGroupChanged(SchedulerGroupDoc? value) => RebuildRows();

        private void RebuildRows()
        {
            TaskRows.Clear();
            if (SelectedGroup != null)
                foreach (var t in SelectedGroup.Tasks) TaskRows.Add(new TaskRow(t));
        }

        [RelayCommand]
        private void Reload()
        {
            Groups.Clear();
            foreach (var g in SchedulerGroupService.List()) Groups.Add(g);
            SelectedGroup = Groups.FirstOrDefault();
        }

        /// <summary>重扫脚本目录（UserData\Scripts）。</summary>
        [RelayCommand]
        private void RefreshScripts()
        {
            AvailableScripts.Clear();
            Directory.CreateDirectory(SchedulerGroupService.ScriptsDir);
            foreach (var f in Directory.GetFiles(SchedulerGroupService.ScriptsDir, "*.js"))
                AvailableScripts.Add(Path.GetFileName(f));
        }

        [RelayCommand]
        private void AddGroup()
        {
            var n = 1;
            while (Groups.Any(g => g.Name == $"配置组{n}")) n++;
            var g = new SchedulerGroupDoc { Name = $"配置组{n}" };
            SchedulerGroupService.Save(g);
            Groups.Add(g);
            SelectedGroup = g;
        }

        [RelayCommand]
        private void DeleteGroup(SchedulerGroupDoc? g)
        {
            if (g == null || IsRunning) return;
            SchedulerGroupService.Delete(g.Name);
            Groups.Remove(g);
            SelectedGroup = Groups.FirstOrDefault();
        }

        /// <summary>重命名分组（双击触发，代码后置会传 SelectedGroup）。</summary>
        [RelayCommand]
        private void RenameGroup(SchedulerGroupDoc? g)
        {
            if (g == null || IsRunning) return;
            var newName = RenameDialog.Show("重命名分组", g.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == g.Name) return;
            if (Groups.Any(x => x.Name == newName))
            { _log.LogWarning($"[调度器] 分组名「{newName}」已存在"); return; }

            SchedulerGroupService.Delete(g.Name);
            g.Name = newName;
            SchedulerGroupService.Save(g);
            var idx = Groups.IndexOf(g);
            Groups.RemoveAt(idx);
            Groups.Insert(idx, g);
            SelectedGroup = g;
        }

        [RelayCommand]
        private void AddScriptTask()
        {
            if (SelectedGroup == null || PickedScript == null) return;
            SelectedGroup.Tasks.Add(new SchedulerTaskItem
            { Name = Path.GetFileNameWithoutExtension(PickedScript), Type = "JsScript", Target = PickedScript });
            SchedulerGroupService.Save(SelectedGroup);
            RebuildRows();
        }

        [RelayCommand]
        private void RemoveTask(TaskRow? row)
        {
            if (SelectedGroup == null || row == null) return;
            SelectedGroup.Tasks.Remove(row.Item);
            SchedulerGroupService.Save(SelectedGroup);
            RebuildRows();
        }

        [RelayCommand]
        private async Task RunGroupAsync()
        {
            if (SelectedGroup == null || IsRunning) return;
            if (!EngineGuard.EnsureReady()) return;   // ★ 引擎守卫
            try { await _runner.RunGroupAsync(SelectedGroup.Name, CancellationToken.None); }
            catch (Exception ex) { _log.LogError($"[调度] 执行失败: {ex.Message}"); }
        }

        [RelayCommand]
        private async Task RunAllAsync()
        {
            if (IsRunning || Groups.Count == 0) return;
            if (!EngineGuard.EnsureReady()) return;   // ★ 引擎守卫
            try { await _runner.RunGroupsAsync(Groups.Select(g => g.Name), CancellationToken.None); }
            catch (Exception ex) { _log.LogError($"[调度] 连续执行失败: {ex.Message}"); }
        }

        [RelayCommand] private void Stop() => _runner.Stop();
        [RelayCommand] private void SkipCurrent() => _runner.SkipCurrent();

        /// <summary>打开调度器文档（Assets\Docs）。</summary>
        [RelayCommand]
        private void OpenDoc()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Assets", "Docs", "SchedulerUsageGuide.md");
            var md = File.Exists(path) ? File.ReadAllText(path) : "# 文档缺失\r\n" + path;
            new DocViewerWindow("调度器使用指南", md).Show();
        }
    }
}
