using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrustNTE.Helpers;
using RadioButton = System.Windows.Controls.RadioButton;

namespace TrustNTE.Views.Pages.Components
{
    /// <summary>
    /// 角色槽位选择器：Toggle 槽位 + 弹出面板（搜索/稀有度/属性筛选/占用灰化）。
    /// 交互还原自 battle-strategy.htm：搜索不关弹层、占用项灰化禁用、None 首项、per-slot 筛选记忆。
    /// </summary>
    public partial class CharacterSlotPicker : UserControl
    {
        public static readonly DependencyProperty SelectedCharacterProperty = DependencyProperty.Register(
            nameof(SelectedCharacter), typeof(string), typeof(CharacterSlotPicker),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public string SelectedCharacter
        { get => (string)GetValue(SelectedCharacterProperty); set => SetValue(SelectedCharacterProperty, value); }

        /// <summary>父级注入：实时查询其他槽位已占用角色（用于灰化禁用）</summary>
        public Func<IEnumerable<string>>? UsedByOthersProvider { get; set; }

        /// <summary>用户提交选择（含清空→null）；父级做去重广播（后选者胜）</summary>
        public event EventHandler<string?>? Committed;

        private string _rarity = "全部";
        private string _element = "全部";
        private readonly ObservableCollection<Row> _rows = new();
        private bool _syncing;

        public CharacterSlotPicker()
        {
            InitializeComponent();
            CharList.ItemsSource = _rows;
        }

        private sealed class Row
        {
            public string DisplayName { get; init; } = "";
            public string MetaText { get; init; } = "";
            public bool IsEnabled { get; init; } = true;
            public string Value { get; init; } = "";
        }

        private void Rebuild()
        {
            var used = UsedByOthersProvider?.Invoke() ?? Enumerable.Empty<string>();
            var search = SearchBox.Text;
            _rows.Clear();

            try
            {
                _rows.Add(new Row { DisplayName = "None (清空)", Value = "" });
                foreach (var c in CharacterCatalog.Filter(_rarity, _element).OrderBy(c => c.Name))
                {
                    if (!string.IsNullOrEmpty(search) && !c.Name.Contains(search)) continue;
                    bool occupied = used.Contains(c.Name);
                    _rows.Add(new Row
                    {
                        DisplayName = c.Name,
                        MetaText = $"{string.Join("+", c.Rarities)} | {c.Element}",
                        IsEnabled = !occupied,
                        Value = c.Name
                    });
                }
            }
            catch (Exception ex)
            {
                // 名册加载失败不再无声——直接把错误显示在列表首行
                _rows.Clear();
                _rows.Add(new Row { DisplayName = $"⚠ {ex.Message}", IsEnabled = false });
            }
        }

        // ── 弹层开关同步（Toggle↔Popup 经典三件套：防止外部点击关闭后 IsChecked 残留 true）──
        private void SlotToggle_Click(object sender, RoutedEventArgs e)
        { if (!_syncing) SlotPopup.IsOpen = SlotToggle.IsChecked == true; }

        private void SlotPopup_Closed(object? sender, EventArgs e)
        { if (!_syncing) { _syncing = true; SlotToggle.IsChecked = false; _syncing = false; } }

        private void SlotPopup_Opened(object? sender, EventArgs e)
        {
            Rebuild();
            // 对应你 htm 的 input.focus() + setSelectionRange：搜索框聚焦且光标在尾
            Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.CaretIndex = SearchBox.Text.Length; });
        }

        // ── 过滤（per-slot 独立记忆）──
        private void Search_Changed(object sender, TextChangedEventArgs e) => Rebuild();

        private void Rarity_Checked(object sender, RoutedEventArgs e)
        { if (sender is RadioButton rb) { _rarity = rb.Tag as string ?? "全部"; Rebuild(); } }

        private void Element_Checked(object sender, RoutedEventArgs e)
        { if (sender is RadioButton rb) { _element = rb.Tag as string ?? "全部"; Rebuild(); } }

        // ── 提交 ──
        private void Row_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem { DataContext: Row row } || !row.IsEnabled) return;
            SelectedCharacter = row.Value;
            Committed?.Invoke(this, string.IsNullOrEmpty(row.Value) ? null : row.Value);
            _syncing = true;
            SlotPopup.IsOpen = false;
            SlotToggle.IsChecked = false;
            _syncing = false;
        }
    }
}