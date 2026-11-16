using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrustNTE.Services;
using TrustNTE.Services.Interfaces;
using TrustNTE.Services.Scripting;
using TrustNTE.Views.Windows;

namespace TrustNTE.ViewModels.Pages
{
    public partial class JsScriptViewModel : ObservableObject
    {
        /// <summary>脚本目录（与 SchedulerGroupService.ScriptsDir / ScriptEngineService 同路径）。</summary>
        public static string ScriptsDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "Scripts");

        private readonly ScriptEngineService _jsEngine;
        private readonly ILogService _log;

        public ObservableCollection<string> Files { get; } = new();

        [ObservableProperty] private string? _selectedFile;
        [ObservableProperty] private string _editorText = "";
        [ObservableProperty] private string _consoleText = "";
        [ObservableProperty] private string _title = "未选择文件";
        [ObservableProperty] private bool _isScriptRunning;   // ★ 运行状态：驱动停止按钮亮/灰

        public JsScriptViewModel(ScriptEngineService jsEngine, ILogService log)
        {
            _jsEngine = jsEngine;
            _log = log;
            _jsEngine.OnJsLog += (level, msg) => Append($"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}");
            Refresh();
        }

        partial void OnSelectedFileChanged(string? value)
        {
            if (value == null) { Title = "未选择文件"; return; }
            try
            {
                EditorText = File.ReadAllText(Path.Combine(ScriptsDir, value));
                Title = value;
            }
            catch (Exception ex) { Append($"[error] 读取失败: {ex.Message}"); }
        }

        private void Append(string line)
        {
            void Apply() => ConsoleText += line + Environment.NewLine;
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) Apply(); else d.BeginInvoke(Apply);
        }

        [RelayCommand]
        private void Refresh()
        {
            Directory.CreateDirectory(ScriptsDir);
            var sel = SelectedFile;
            Files.Clear();
            foreach (var f in Directory.GetFiles(ScriptsDir, "*.js"))
                Files.Add(Path.GetFileName(f));
            SelectedFile = sel != null && Files.Contains(sel) ? sel : Files.FirstOrDefault();
        }

        [RelayCommand]
        private void NewFile()
        {
            if (IsScriptRunning) { Append("[error] 运行中，先停止"); return; }
            var n = 1;
            while (Files.Contains($"script{n}.js")) n++;
            var name = $"script{n}.js";
            File.WriteAllText(Path.Combine(ScriptsDir, name),
                "// 在这里编写 JS 脚本\r\n// 可用: log.info/error/debug, automation.Press/Click/PressQ/Attack/LockTarget/SwitchNext, sleep\r\n// 支持顶层 await 与顶层 return\r\n");
            Refresh();
            SelectedFile = name;
        }

        [RelayCommand]
        private void Save()
        {
            if (SelectedFile == null) { Append("[error] 未选择文件"); return; }
            if (IsScriptRunning) { Append("[error] 运行中，先停止再保存"); return; }
            if (!_jsEngine.TryCompile(EditorText, out var err))
            {
                Append($"[error] 语法错误，未保存: {err}");
                return;
            }
            File.WriteAllText(Path.Combine(ScriptsDir, SelectedFile), EditorText);
            Title = SelectedFile;
            Append($"[{DateTime.Now:HH:mm:ss}] [info] 已保存 {SelectedFile}");
        }

        [RelayCommand]
        private async Task RunAsync()
        {
            if (SelectedFile == null) { Append("[error] 未选择文件"); return; }
            if (IsScriptRunning) { Append("[warn] 已有脚本在跑，先点 ⏹ 停止"); return; }
            if (!EngineGuard.EnsureReady()) return;

            File.WriteAllText(Path.Combine(ScriptsDir, SelectedFile), EditorText);   // 跑的就是看到的内容
            Append($"[{DateTime.Now:HH:mm:ss}] ▶ 运行 {SelectedFile} ──");
            IsScriptRunning = true;
            try
            {
                var result = await _jsEngine.ExecuteScriptAsync(EditorText, 0, CancellationToken.None);
                Append($"[{DateTime.Now:HH:mm:ss}] ✅ 完成 | 返回: {result}");
            }
            catch (OperationCanceledException) { Append($"[{DateTime.Now:HH:mm:ss}] ⏹ 已停止"); }
            catch (TimeoutException) { Append($"[{DateTime.Now:HH:mm:ss}] ⏱ 执行超时"); }
            catch (Exception ex) { Append($"[{DateTime.Now:HH:mm:ss}] ❌ {ex.Message}"); }
            finally { IsScriptRunning = false; }
        }

        /// <summary>★ 停止当前脚本（立即返回，脚本在后台被中断）。</summary>
        [RelayCommand]
        private void Stop()
        {
            _jsEngine.StopScript();
            Append($"[{DateTime.Now:HH:mm:ss}] ⏹ 停止指令已发送");
        }

        [RelayCommand]
        private void OpenFolder()
        {
            Directory.CreateDirectory(ScriptsDir);
            Process.Start(new ProcessStartInfo { FileName = ScriptsDir, UseShellExecute = true });
        }

        [RelayCommand]
        private void OpenDoc()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Assets", "Docs", "JavaScriptSearchScriptDevelopmentGuide.md");
            var md = File.Exists(path) ? File.ReadAllText(path) : "# 文档缺失\r\n" + path;
            new DocViewerWindow("JS 脚本开发指南", md).Show();
        }
    }
}
