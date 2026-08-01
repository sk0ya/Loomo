namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: 分割表示の保存配置（自由タイル配置に名前を付けたもの）の保存・呼び出し・巡回。 ワークスペース毎に保持し、タイトルバーのビュー・スイッチャーから切り替える。Ctrl+T で巡回する（未保存の変更は単一スクラッチ枠へ退避）。巡回の純ロジックは <see cref="LayoutCycleLogic"/>。</summary>
public partial class ShellWindow {
    private readonly List<SavedLayout> _layouts = new();
    private PaneNodeSnapshot? _scratchLayout;
    private int _activeLayoutIndex = -1;
    private bool _layoutDirty;
    private void LoadLayouts(IEnumerable<SavedLayout> layouts, PaneNodeSnapshot? scratch, int activeIndex, bool dirty) {
        _layouts.Clear();
        _layouts.AddRange(layouts);
        if (_layouts.Count == 0)
            _layouts.AddRange(SavedLayout.Defaults());
        _scratchLayout = scratch;
        _activeLayoutIndex = activeIndex;
        _layoutDirty = dirty;
        UpdateModeButtons();
    }
    /// <summary>保存済みの配置に一致していないときのラベル。<see cref="ShellWindow.UpdateMainPaneHeader"/> が
    /// ヘッダー向けの言い回しへ差し替えるので、判定はこの定数と突き合わせる。</summary>
    private const string UnsavedLayoutLabel = "（未保存）";
    private void UpdateModeButtons() {
        if (MainPaneButton is null)   // InitializeComponent 前のガード
            return;
        ApplyModeChoiceState(SplitModeChoice, SplitModeIcon, SplitModeText, !_stageActive);
        ApplyModeChoiceState(ConcentratedModeChoice, ConcentratedModeIcon, ConcentratedModeText, _stageActive);
        DisplayModeLabel.Text = DisplayModeName(_stageActive);
        // 1行に収める。折り返すとショートカットが途中で割れて読めなくなる。
        ModeDescription.Text = (_stageActive
            ? "1つを大きく、ほかは右側で待機。"
            : "複数の画面をタイル状に並べる。")
            + ShortcutSuffix("mode.toggle", "で切り替え");
        LayoutCycleHint.Text = ShortcutHint("stage.cycle", "で順に切り替え");
        UpdateMainPaneHeader();
    }
    /// <summary>割り当てが無ければ何も出さない（未割り当てのキーを案内しないため）。</summary>
    private string ShortcutHint(string commandId, string suffix)
        => _keybindings.For(commandId)?.Format() is { Length: > 0 } key ? $"{key} {suffix}" : "";
    private string ShortcutSuffix(string commandId, string suffix)
        => ShortcutHint(commandId, suffix) is { Length: > 0 } hint ? $"（{hint}）" : "";
    /// <summary>表示モードのセグメント2択。選択中は面で塗り、アイコンと字面もアクセントへ寄せる
    /// （小さな印だけだと、どちらに居るのかポップアップを開くたび探すことになる）。</summary>
    private void ApplyModeChoiceState(Button choice, System.Windows.Shapes.Path icon, TextBlock text, bool active) {
        choice.Background = active ? (Brush)FindResource("SelectionBg") : Brushes.Transparent;
        icon.Stroke = (Brush)FindResource(active ? "Accent" : "FgDim");
        text.Foreground = (Brush)FindResource(active ? "Accent" : "Fg");
    }
    private string CurrentLayoutLabel() {
        if (!_layoutDirty && _activeLayoutIndex >= 0 && _activeLayoutIndex < _layouts.Count)
            return _layouts[_activeLayoutIndex].Name;
        return UnsavedLayoutLabel;
    }
    private void CycleLayout(int direction) {
        if (_stageActive)
            return;
        CaptureLayoutSizes();
        if ((_layoutDirty || _activeLayoutIndex < 0) && _root is not null) {
            var current = ToSnapshot(_root);
            var sameAsSaved = _layouts.FindIndex(l => PaneLayoutTree.SnapshotsEquivalent(l.Tree, current));
            if (sameAsSaved >= 0) {
                _activeLayoutIndex = sameAsSaved;
                _layoutDirty = false;
            } else {
                _scratchLayout = current;
            }
        }
        var next = LayoutCycleLogic.NextIndex( _activeLayoutIndex, _layouts.Count, _scratchLayout is not null, direction);
        if (next == _activeLayoutIndex && !_layoutDirty)
            return;   // 1枚しかない等、行き先が無い
        LoadLayoutAt(next);
        SaveActiveWorkspaceSnapshot();
    }
    private void LoadLayoutAt(int index) {
        BeginTrailLayoutChange();
        var snapshot = index < 0
            ? _scratchLayout
            : index < _layouts.Count ? _layouts[index].Tree : null;
        _activeLayoutIndex = index;
        _layoutDirty = false;
        ApplyPaneLayout(snapshot);
        UpdateModeButtons();
        if (AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind is { } first)
            FocusPane(first);
    }
    private void LoadLayout(int index) {
        if (index < 0 || index >= _layouts.Count || _stageActive)
            return;
        LoadLayoutAt(index);
        SaveActiveWorkspaceSnapshot();
    }
    private void SaveCurrentLayout(string name) {
        name = name.Trim();
        if (name.Length == 0 || _stageActive || _root is null)
            return;
        CaptureLayoutSizes();
        var layout = new SavedLayout { Name = name, Tree = ToSnapshot(_root) };
        var existing = _layouts.FindIndex(p => p.Name == name);
        if (existing >= 0) {
            _layouts[existing] = layout;
            _activeLayoutIndex = existing;
        } else {
            _layouts.Add(layout);
            _activeLayoutIndex = _layouts.Count - 1;
        }
        _layoutDirty = false;
        UpdateModeButtons();
        SaveActiveWorkspaceSnapshot();
    }
    private void DeleteLayout(int index) {
        if (index < 0 || index >= _layouts.Count)
            return;
        _layouts.RemoveAt(index);
        if (_activeLayoutIndex == index)
            _activeLayoutIndex = -1;
        else if (_activeLayoutIndex > index)
            _activeLayoutIndex--;
        UpdateModeButtons();
        SaveActiveWorkspaceSnapshot();
    }
    private void BuildLayoutPopup() {
        LayoutPopupList.Children.Clear();
        LayoutEmptyHint.Visibility = _layouts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var accent = (Brush)FindResource("Accent");
        var fg = (Brush)FindResource("Fg");
        var fgDim = (Brush)FindResource("FgDim");
        for (var i = 0; i < _layouts.Count; i++) {
            var index = i;
            var layout = _layouts[i];
            var row = new DockPanel { LastChildFill = true };
            var del = new Button {
                Content = "×", FontSize = UiFontManager.Scaled(12), ToolTip = "この配置を削除",
                Width = 26, Visibility = Visibility.Hidden, Style = (Style)FindResource("BranchMenuItem"), };
            del.Click += (_, _) => {
                DeleteLayout(index);
                BuildLayoutPopup();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            var item = BuildPopupRow(layout.Name, LayoutRowIcon);
            item.SetState(!_layoutDirty && index == _activeLayoutIndex, enabled: true, accent, fg, fgDim);
            item.Button.ToolTip = LayoutSummary(layout);
            item.Button.Click += (_, _) => {
                PaneTogglePopup.IsOpen = false;
                LoadLayout(index);
            };
            row.Children.Add(item.Button);
            row.MouseEnter += (_, _) => del.Visibility = Visibility.Visible;
            row.MouseLeave += (_, _) => del.Visibility = Visibility.Hidden;
            LayoutPopupList.Children.Add(row);
        }
    }
    /// <summary>配置行のアイコン（タイル分割の見た目）。ペインのアイコンと同じ枠に収める。</summary>
    private static readonly Geometry LayoutRowIcon = CreateLayoutRowIcon();
    private static Geometry CreateLayoutRowIcon() {
        var geometry = Geometry.Parse("M0.5,1.5 H6.5 V12.5 H0.5 Z M8.5,1.5 H14.5 V6.5 H8.5 Z M8.5,8 H14.5 V12.5 H8.5 Z");
        geometry.Freeze();
        return geometry;
    }
    private void OnLayoutSaveToggle(object sender, RoutedEventArgs e) {
        var show = LayoutSaveRow.Visibility != Visibility.Visible;
        LayoutSaveRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
            LayoutNameInput.Focus();
    }
    private void OnLayoutNameKeyDown(object sender, KeyEventArgs e) {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        OnLayoutSaveClick(sender, e);
    }
    private static string LayoutSummary(SavedLayout layout) {
        var panes = LeafKinds(layout.Tree).Select(PaneLabel);
        return $"{layout.Name}  ({string.Join(" · ", panes)})";
    }
    private static IEnumerable<PaneKind> LeafKinds(PaneNodeSnapshot node) {
        if (node.Kind is { } kind) {
            yield return kind;
            yield break;
        }
        foreach (var child in node.Children)
            foreach (var k in LeafKinds(child))
                yield return k;
    }
    private void OnLayoutSaveClick(object sender, RoutedEventArgs e) {
        var name = LayoutNameInput.Text;
        if (string.IsNullOrWhiteSpace(name))
            name = $"配置 {_layouts.Count + 1}";
        SaveCurrentLayout(name);
        LayoutNameInput.Clear();
        LayoutSaveRow.Visibility = Visibility.Collapsed;
        BuildLayoutPopup();
    }
}
