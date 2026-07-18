
namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: レイアウトモードの保存レイアウト（自由タイル配置に名前を付けたもの）の保存・呼び出し・巡回。
/// ワークスペース毎に保持し、タイトルバーの「📐 レイアウト」ドロップダウンから切り替える。Ctrl+T で
/// 巡回する（未保存の変更は単一スクラッチ枠へ退避）。巡回の純ロジックは <see cref="LayoutCycleLogic"/>。
/// </summary>
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

    private void UpdateModeButtons() {
        if (StageModeToggle is not null)
            StageModeToggle.IsChecked = _stageActive;

        if (LayoutButton is null)   // InitializeComponent 前のガード
            return;
        LayoutButton.Visibility = _stageActive ? Visibility.Collapsed : Visibility.Visible;
        LayoutButtonLabel.Text = CurrentLayoutLabel();
    }

    private string CurrentLayoutLabel() {
        if (!_layoutDirty && _activeLayoutIndex >= 0 && _activeLayoutIndex < _layouts.Count)
            return _layouts[_activeLayoutIndex].Name;
        return "（未保存）";
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

        var next = LayoutCycleLogic.NextIndex(
            _activeLayoutIndex, _layouts.Count, _scratchLayout is not null, direction);
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

    private void OnLayoutMenuClick(object sender, RoutedEventArgs e) {
        if (_stageActive)
            return;
        BuildLayoutPopup();
        LayoutPopup.IsOpen = true;
    }

    private void BuildLayoutPopup() {
        LayoutPopupList.Children.Clear();

        if (_layouts.Count == 0) {
            LayoutPopupList.Children.Add(new TextBlock {
                Text = "保存したレイアウトはまだありません",
                FontSize = UiFontManager.Scaled(12),
                Margin = new Thickness(10, 6, 10, 6),
                Foreground = (Brush)FindResource("FgDim"),
            });
            return;
        }

        for (var i = 0; i < _layouts.Count; i++) {
            var index = i;
            var layout = _layouts[i];
            var row = new DockPanel { LastChildFill = true };

            var del = new Button {
                Content = "✕",
                FontSize = UiFontManager.Scaled(11),
                ToolTip = "このレイアウトを削除",
                Width = 28,
                Style = (Style)FindResource("BranchMenuItem"),
            };
            del.Click += (_, _) => {
                DeleteLayout(index);
                BuildLayoutPopup();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            var active = !_layoutDirty && index == _activeLayoutIndex;
            var load = new Button {
                Style = (Style)FindResource("BranchMenuItem"),
                FontSize = UiFontManager.Scaled(12),
                Content = new TextBlock {
                    Text = LayoutSummary(layout),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = active ? (Brush)FindResource("Accent") : (Brush)FindResource("Fg"),
                },
            };
            load.Click += (_, _) => {
                LayoutPopup.IsOpen = false;
                LoadLayout(index);
            };
            row.Children.Add(load);

            LayoutPopupList.Children.Add(row);
        }
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
            name = $"レイアウト {_layouts.Count + 1}";
        SaveCurrentLayout(name);
        LayoutNameInput.Clear();
        BuildLayoutPopup();
    }
}
