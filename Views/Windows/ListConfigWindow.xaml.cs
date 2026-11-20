using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrustNTE.Helpers;
using Wpf.Ui.Controls;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace TrustNTE.Views.Windows
{
    /// <summary>
    /// 黑白名单配置窗口即模式（互斥单选）、独立存储、模糊/精准搜索定位。
    /// </summary>
    public partial class ListConfigWindow : FluentWindow
    {
        private readonly List<int> _hits = new();
        private int _hitCursor = -1;
        private readonly Brush _defaultSearchBorder;

        /// <summary>保存结果：模式（"白名单模式" / "黑名单模式"）。</summary>
        public string FinalListMode { get; private set; } = "白名单模式";
        /// <summary>保存结果：白名单多行文本（已清理空行，保留原样）。</summary>
        public string FinalWhiteText { get; private set; } = "";
        /// <summary>保存结果：黑名单多行文本。</summary>
        public string FinalBlackText { get; private set; } = "";

        public ListConfigWindow(string listMode, string whitelistText, string blacklistText)
        {
            InitializeComponent();
            _defaultSearchBorder = SearchBox.BorderBrush;

            WhiteBox.Text = whitelistText ?? "";
            BlackBox.Text = blacklistText ?? "";

            // tab = 模式：切到哪个 tab，保存时就是哪个模式
            if (listMode == "黑名单模式") TabBlack.IsChecked = true;
            else TabWhite.IsChecked = true;

            Loaded += (_, _) => SearchBox.Focus();
        }

        // ---------- 文档 ----------

        private void OpenDoc_Click(object sender, RoutedEventArgs e)
        {
            DocViewerWindow.ShowDoc("黑白名单使用说明", "auto_loot_lists.md");
        }

        // ---------- 页签（tab 即模式） ----------

        private TextBox CurrentBox => TabBlack.IsChecked == true ? BlackBox : WhiteBox;

        private void Tab_Changed(object sender, RoutedEventArgs e)
        {
            if (WhiteBox == null || HitLabel == null) return; 
            bool black = TabBlack.IsChecked == true;
            PanelBlack.Visibility = black ? Visibility.Visible : Visibility.Collapsed;
            PanelWhite.Visibility = black ? Visibility.Collapsed : Visibility.Visible;
            DescText.Text = black
                ? "【黑名单模式】会拾取所有内容，但排除列表中的物品。示例：点击上方文档进行查看"
                : "【白名单模式】默认拾取列表中的内容，未列出的物品不会拾取。示例：点击上方文档进行查看";
            ResetSearch();
        }

        // ---------- 搜索 ----------

        private bool Fuzzy => ModeFuzzy.IsChecked == true;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ResetSearch();
        private void Mode_Changed(object sender, RoutedEventArgs e) => ResetSearch();

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Jump(findNext: Keyboard.Modifiers != ModifierKeys.Shift);
                e.Handled = true;
            }
        }
        private void BtnNext_Click(object sender, RoutedEventArgs e) => Jump(true);
        private void BtnPrev_Click(object sender, RoutedEventArgs e) => Jump(false);

        private void ResetSearch()
        {
            //   解析期守卫：ModeFuzzy 在 XAML 里 IsChecked="True"，InitializeComponent 解析到它时
            //   会同步触发 Checked → Mode_Changed → ResetSearch，此刻 HitLabel 尚未创建。
            if (HitLabel == null || SearchBox == null || ModeFuzzy == null) return;

            _hits.Clear();
            _hitCursor = -1;
            HitLabel.Text = "";
            SearchBox.BorderBrush = _defaultSearchBorder;

            string query = PickupListEvaluator.Normalize(SearchBox.Text);
            if (query.Length == 0) return;

            string text = CurrentBox.Text;
            string norm = PickupListEvaluator.Normalize(text);  

            if (Fuzzy)
            {
                int idx = 0;
                while ((idx = norm.IndexOf(query, idx, StringComparison.Ordinal)) >= 0)
                { _hits.Add(idx); idx += query.Length; }
            }
            else
            {
                // 精准 = 整行归一化后相等；命中定位到行首、选中整行
                int lineStart = 0;
                foreach (string line in text.Split('\n'))
                {
                    if (PickupListEvaluator.Normalize(line) == query)
                        _hits.Add(lineStart);
                    lineStart += line.Length + 1;
                }
            }

            if (_hits.Count == 0)
            {
                HitLabel.Text = "无结果";
                SearchBox.BorderBrush = Brushes.IndianRed;
                return;
            }
            HitLabel.Text = $"1/{_hits.Count}";
            Show(0);
        }

        private void Jump(bool findNext)
        {
            if (_hits.Count == 0) { ResetSearch(); if (_hits.Count > 0) Show(0); return; }
            _hitCursor = findNext
                ? (_hitCursor + 1) % _hits.Count
                : (_hitCursor - 1 + _hits.Count) % _hits.Count;
            HitLabel.Text = $"{_hitCursor + 1}/{_hits.Count}";
            Show(_hitCursor);
        }

        private void Show(int hitIndex)
        {
            int start = _hits[hitIndex];
            int length = Fuzzy
                ? PickupListEvaluator.Normalize(SearchBox.Text).Length
                : LineLengthAt(start);
            CurrentBox.Focus();
            CurrentBox.Select(start, Math.Max(1, length));
            CurrentBox.CaretIndex = start;
        }

        private int LineLengthAt(int lineStart)
        {
            string t = CurrentBox.Text;
            int i = lineStart;
            while (i < t.Length && t[i] != '\n' && t[i] != '\r') i++;
            return i - lineStart;
        }

        // ---------- 保存 / 取消 ----------

        // 所见即所存：只做 Trim + 去空行，不转全角不去重（归一化只发生在判定和搜索时）
        private static string Clean(string text)
            => string.Join(Environment.NewLine,
               (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.Trim())
                           .Where(l => l.Length > 0));

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            FinalListMode = TabBlack.IsChecked == true ? "黑名单模式" : "白名单模式";
            FinalWhiteText = Clean(WhiteBox.Text);
            FinalBlackText = Clean(BlackBox.Text);
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void WhiteBox_TextChanged(object sender, TextChangedEventArgs e)
        {

        }
    }
}
