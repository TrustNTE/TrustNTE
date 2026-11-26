using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TrustNTE.Helpers;

// 显性别名：GlobalUsings.cs 全局引入了 System.Windows.Forms / System.Drawing，
// 裸写 Brush / KeyEventArgs / Clipboard 会撞名（CS0104）
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Clipboard = System.Windows.Clipboard;

namespace TrustNTE.Views.Controls
{
    public enum LogEntryLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

    public sealed class LogEntry
    {
        public string Text { get; }
        public LogEntryLevel Level { get; }
        public Brush Brush { get; }

        public LogEntry(string text, LogEntryLevel level, Brush brush)
        {
            Text = text; Level = level; Brush = brush;
        }
    }

    public partial class LogConsoleControl : UserControl
    {
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(LogConsoleControl),
            new PropertyMetadata("运行日志", OnTitleChanged));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LogConsoleControl c) c.TitleText.Text = e.NewValue as string;
        }

        private readonly ObservableCollection<LogEntry> _entries = new();
        private readonly ICollectionView _view;
        private readonly List<LogEntry> _pending = new();
        private readonly object _gate = new();
        private DispatcherTimer? _flushTimer;
        private ScrollViewer? _listScrollViewer;
        private bool _atBottom = true;

        /// <summary>缓冲上限（条）。虚拟化下 2000 条无压力。</summary>
        public int MaxVisibleLines { get; set; } = 2000;

        /// <summary>显示过滤下限。仅影响显示，不影响生成与复制。</summary>
        public LogEntryLevel MinLevel { get; set; } = LogEntryLevel.Debug;

        /// <summary>★ DEBUG专用chip：true 时忽略 MinLevel，仅显示 Debug 级条目。</summary>
        public bool DebugOnly { get; private set; }

        public LogConsoleControl()
        {
            InitializeComponent();
            _view = CollectionViewSource.GetDefaultView(_entries);
            _view.Filter = o => o is LogEntry e && Matches(e);
            LogList.ItemsSource = _view;

            _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _flushTimer.Tick += (_, _) => FlushLogs();
            this.Loaded += (_, _) => { _flushTimer?.Start(); EnsureScrollViewerTracked(); };
            this.Unloaded += (_, _) => _flushTimer?.Stop();
        }

        public void EnqueueLine(string line)
        {
            lock (_gate)
            {
                _pending.Add(Parse(line));
                if (_pending.Count > 500) _pending.RemoveAt(0);
            }
        }

        public void Clear()
        {
            lock (_gate) _pending.Clear();
            _entries.Clear();
        }

        private static LogEntry Parse(string line) => new(line, DetectLevel(line), LogPalette.Pick(line));

        private static LogEntryLevel DetectLevel(string line)
        {
            // 实锤格式："[16:39:18.003] [INFO] 正文"
            int first = line.IndexOf(']');
            if (first <= 0 || first + 2 >= line.Length || line[first + 1] != ' ' || line[first + 2] != '[')
                return LogEntryLevel.Info;
            int end = line.IndexOf(']', first + 2);
            if (end < 0) return LogEntryLevel.Info;
            var tag = line.Substring(first + 3, end - first - 3);
            if (tag.Equals("DEBUG", StringComparison.OrdinalIgnoreCase)) return LogEntryLevel.Debug;
            if (tag.Equals("WARN", StringComparison.OrdinalIgnoreCase)) return LogEntryLevel.Warn;
            if (tag.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) return LogEntryLevel.Error;
            return LogEntryLevel.Info;
        }

        private void FlushLogs()
        {
            LogEntry[] batch;
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                batch = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var entry in batch) _entries.Add(entry);
            while (_entries.Count > MaxVisibleLines) _entries.RemoveAt(0);
            if (_atBottom && _entries.Count > 0) LogList.ScrollIntoView(_entries[^1]);
        }

        private void EnsureScrollViewerTracked()
        {
            if (_listScrollViewer != null) return;
            _listScrollViewer = FindScrollViewer(LogList);
            if (_listScrollViewer != null)
                _listScrollViewer.ScrollChanged += (_, e) =>
                {
                    if (e.ExtentHeightChange == 0)
                        _atBottom = _listScrollViewer.VerticalOffset + _listScrollViewer.ViewportHeight >= _listScrollViewer.ExtentHeight - 24;
                };
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer sv) return sv;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var r = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>切换显示过滤（级别 chips 调用）。debugOnly=true 时只看 DEBUG 级条目。</summary>
        public void ApplyMinLevel(LogEntryLevel level, bool debugOnly = false)
        {
            MinLevel = level;
            DebugOnly = debugOnly;
            _view.Refresh();
        }

        /// <summary>★ 统一过滤判定：DebugOnly 模式只看DEBUG级；否则按下限过滤。</summary>
        private bool Matches(LogEntry e)
            => DebugOnly ? e.Level == LogEntryLevel.Debug : e.Level >= MinLevel;

        private void CopySelected_Click(object sender, RoutedEventArgs e) => CopySelected();
        private void SelectAll_Click(object sender, RoutedEventArgs e) => LogList.SelectAll();

        private void LogList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && LogList.SelectedItems.Count > 0)
            {
                CopySelected();
                e.Handled = true;
            }
        }

        public void CopySelected()
        {
            if (LogList.SelectedItems.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (var item in LogList.SelectedItems)
                if (item is LogEntry le) sb.AppendLine(le.Text);
            try { Clipboard.SetText(sb.ToString()); } catch { /* 剪贴板被占用，静默 */ }
        }

        /// <summary>全选并复制整个缓冲，返回复制的行数。</summary>
        public int CopyAllBuffered()
        {
            if (_entries.Count == 0) return 0;
            LogList.SelectAll();
            int n = LogList.SelectedItems.Count;
            CopySelected();
            return n;
        }
    }
}
