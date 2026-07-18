namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペインレイアウト（2D並べ替え・ドラッグ移動・ズーム・表示切替・スナップショット適用）</summary>
public partial class ShellWindow {
    private bool _paneSplitterDragging;
    private void InitializePanes() {
        _paneElements[PaneKind.Terminal] = TerminalPane;
        _paneElements[PaneKind.Editor] = EditorPane;
        _paneElements[PaneKind.EditorSupport] = EditorSupportPane;
        _paneElements[PaneKind.Browser] = BrowserPane;
        _paneElements[PaneKind.Ai] = AiPane;
        _paneElements[PaneKind.Git] = GitPane;
        _paneElements[PaneKind.Diff] = DiffPane;
        _paneElements[PaneKind.Trace] = TracePane;
        _paneElements[PaneKind.Debug] = DebugPane;
        _editorViews = new PaneSplitView( EditorContentHost, id => _editorTabs.FirstOrDefault(t => t.Id == id)?.Control, () => _editorTabs.Where(t => t.IsRealized).Select(t => (FrameworkElement)t.Control), () => (Brush)FindResource("Border"), () => (Brush)FindResource("Accent"), el => el.Focus(), () => SaveActiveWorkspaceSnapshot());
        _terminalViews = new PaneSplitView( TerminalContentHost, id => _terminalTabs.FirstOrDefault(t => t.Id == id)?.View, () => _terminalTabs.Select(t => (FrameworkElement)t.View), () => (Brush)FindResource("Border"), () => (Brush)FindResource("Accent"), el => { if (el is TerminalTabView tv) tv.FocusTerminal(); else el.Focus(); }, () => SaveActiveWorkspaceSnapshot());
    }
    private void ApplyDefaultLayout() {
        _zoomedPane = null;
        var top = new PaneSplit { Orientation = SplitKind.Columns, Weight = 2 };
        top.Children.Add(NewLeaf(PaneKind.Editor));
        top.Children.Add(new PaneLeaf { Kind = PaneKind.EditorSupport, Hidden = true });
        top.Children.Add(NewLeaf(PaneKind.Browser));
        var root = new PaneSplit { Orientation = SplitKind.Rows };
        root.Children.Add(top);
        root.Children.Add(NewLeaf(PaneKind.Terminal));
        root.Children.Add(NewLeaf(PaneKind.Ai));
        _root = root;
        RebuildPaneLayout();
    }
    private PaneLeaf NewLeaf(PaneKind kind) => new() { Kind = kind };
    private IEnumerable<PaneLeaf> AllLeaves(PaneNode? node = null) => _paneLayout.Leaves(node);
    private PaneLeaf? FindLeaf(PaneKind kind) => _paneLayout.Find(kind);
    private PaneSplit? FindParent(PaneNode target, PaneNode? current = null)
        => _paneLayout.FindParent(target, current);
    private void RebuildPaneLayout() {
        PaneLayoutDebugLog.Log($"RebuildPaneLayout() stageActive={_stageActive}", withCaller: true);
        if (_stageActive) {
            RebuildStage();
            return;
        }
        CaptureLayoutSizes();
        foreach (var element in _paneElements.Values)
            if (element.Parent is Panel parent)
                parent.Children.Remove(element);
        PaneHost.Children.Clear();
        PaneHost.RowDefinitions.Clear();
        PaneHost.ColumnDefinitions.Clear();
        _root = Normalize(_root);
        if (_root is null) {
            ApplyDefaultLayout();
            return;
        }
        foreach (var leaf in AllLeaves())
            if (!leaf.Hidden)
                _enabledSessions.Add(leaf.Kind);
        UpdatePaneToggleStates();
        if (_zoomedPane is { } zoom) {
            if (FindLeaf(zoom) is { Hidden: false } && _paneElements.TryGetValue(zoom, out var zoomElement)) {
                zoomElement.Visibility = Visibility.Visible;
                PaneHost.Children.Add(zoomElement);
                ScheduleBrowserRealize(_activeBrowserTab);
                ScheduleLayoutWings();
                return;
            }
            _zoomedPane = null; // 対象が隠れた/消えていたらズーム解除して通常描画へ
        }
        var border = (Brush)FindResource("Border");
        var visual = BuildNode(_root, border);
        if (visual is null) {
            ApplyDefaultLayout();
            return;
        }
        PaneHost.Children.Add(visual);
        ScheduleBrowserRealize(_activeBrowserTab);
        ScheduleLayoutWings();
    }
    private FrameworkElement? BuildNode(PaneNode node, Brush border) {
        if (node is PaneLeaf leaf) {
            if (leaf.Hidden)
                return null;
            var element = _paneElements[leaf.Kind];
            element.Visibility = Visibility.Visible;
            return element;
        }
        var split = (PaneSplit)node;
        split.Host = null;
        foreach (var c in split.Children)
            c.TrackIndex = -1;
        var visibleChildren = split.Children.Where(IsNodeVisible).ToList();
        if (visibleChildren.Count == 0)
            return null;
        if (visibleChildren.Count == 1)
            return BuildNode(visibleChildren[0], border);
        var grid = new Grid();
        split.Host = grid;
        var cols = split.Orientation == SplitKind.Columns;
        var min = cols ? 160.0 : 100.0;
        for (var i = 0; i < visibleChildren.Count; i++) {
            if (i > 0) {
                AddTrack(grid, cols, new GridLength(SplitterThickness));
                var splitter = NewSplitter(cols, border, split);
                SetTrack(splitter, cols, i * 2 - 1);
                grid.Children.Add(splitter);
            }
            var child = visibleChildren[i];
            AddTrack(grid, cols, new GridLength(child.Weight <= 0 ? 1 : child.Weight, GridUnitType.Star), min);
            child.TrackIndex = i * 2;
            var visual = BuildNode(child, border);
            SetTrack(visual!, cols, i * 2);
            grid.Children.Add(visual);
        }
        return grid;
    }
    private static bool IsNodeVisible(PaneNode node) => PaneLayoutTree.IsNodeVisible(node);
    private static void AddTrack(Grid grid, bool cols, GridLength length, double min = 0) {
        if (cols)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length, MinWidth = min });
        else
            grid.RowDefinitions.Add(new RowDefinition { Height = length, MinHeight = min });
    }
    private static void SetTrack(UIElement element, bool cols, int index) {
        if (cols)
            Grid.SetColumn(element, index);
        else
            Grid.SetRow(element, index);
    }
    private GridSplitter NewSplitter(bool cols, Brush border, PaneSplit split) {
        var accent = (Brush)FindResource("Accent");
        var splitter = new GridSplitter {
            Width = cols ? SplitterThickness : double.NaN, Height = cols ? double.NaN : SplitterThickness, ResizeDirection = cols ? GridResizeDirection.Columns : GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = border, Cursor = cols ? Cursors.SizeWE : Cursors.SizeNS, ToolTip = "ドラッグでリサイズ／ダブルクリックで均等化"
        };
        splitter.MouseEnter += (_, _) => splitter.Background = accent;
        splitter.MouseLeave += (_, _) => splitter.Background = border;
        splitter.DragStarted += (_, _) => {
            BeginTrailLayoutChange();
            _paneSplitterDragging = true;
        };
        splitter.DragCompleted += (_, _) => {
            _paneSplitterDragging = false;
            PaneLayoutDebugLog.Log($"tile splitter DragCompleted cols={cols} splitWeights=[{string.Join(",", split.Children.Select(c => c.Weight.ToString("0.#")))}]");
            CaptureLayoutSizes();
            PaneLayoutDebugLog.Log($"  after CaptureLayoutSizes splitWeights=[{string.Join(",", split.Children.Select(c => c.Weight.ToString("0.#")))}]");
            MarkLayoutDirty();
            SaveActiveWorkspaceSnapshot();
            ScheduleLayoutWings();
        };
        splitter.MouseDoubleClick += (_, e) => { EqualizeSiblings(split); e.Handled = true; };
        return splitter;
    }
    private void EqualizeSiblings(PaneSplit split) {
        BeginTrailLayoutChange();
        foreach (var child in split.Children)
            child.Weight = 1;
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }
    private void MarkLayoutDirty() {
        if (_stageActive || _layoutDirty)
            return;
        _layoutDirty = true;
        UpdateModeButtons();
    }
    private void ToggleZoom() {
        if (_stageActive) {
            ToggleOverview();
            return;
        }
        if (_zoomedPane is not null) {
            ZoomPane(null);
            return;
        }
        var target = _focusedRegion?.Pane ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind;
        if (target is { } kind)
            ZoomPane(kind);
    }
    private void ToggleZoomFor(PaneKind kind) => ZoomPane(_zoomedPane == kind ? null : kind);
    private void HideFocusedRegion() {
        if (_focusedRegion is { IsSidebar: true }) {
            _vm.IsSidebarVisible = false;
            return;
        }
        if (_stageActive)
            return;
        var target = _focusedRegion?.Pane ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind;
        if (target is { } kind)
            SetPaneVisible(kind, false);
    }
    private void ZoomPane(PaneKind? kind) {
        if (kind is { } k && (!IsPaneVisible(k) || VisibleLeafCount() <= 1))
            return; // 1枚だけ、または隠れているペインはズームしない
        if (_zoomedPane is null && kind is not null)
            CaptureLayoutSizes();
        _zoomedPane = kind;
        RebuildPaneLayout();
        if (kind is { } focus)
            FocusPane(focus);
        else if (_focusedRegion?.Pane is { } prev)
            FocusPane(prev);
    }
    private void CaptureLayoutSizes() => CaptureNode(_root);
    private static void CaptureNode(PaneNode? node) {
        if (node is not PaneSplit split)
            return;
        if (split.Host is { } grid) {
            var cols = split.Orientation == SplitKind.Columns;
            foreach (var child in split.Children)
            {
                var index = child.TrackIndex;
                if (index < 0)
                    continue;
                var oldWeight = child.Weight;
                if (cols) {
                    if (index < grid.ColumnDefinitions.Count) {
                        var definition = grid.ColumnDefinitions[index];
                        child.Weight = definition.ActualWidth > 0
                            ? definition.ActualWidth
                            : PositiveGridLengthValue(definition.Width, child.Weight);
                    }
                } else {
                    if (index < grid.RowDefinitions.Count) {
                        var definition = grid.RowDefinitions[index];
                        child.Weight = definition.ActualHeight > 0
                            ? definition.ActualHeight
                            : PositiveGridLengthValue(definition.Height, child.Weight);
                    }
                }
                if (PaneLayoutDebugLog.Enabled && Math.Abs(oldWeight - child.Weight) > 0.5) {
                    var label = child is PaneLeaf leaf ? leaf.Kind.ToString() : "split";
                    PaneLayoutDebugLog.Log($"    CaptureNode: {label}[{index}] weight {oldWeight:0.#} -> {child.Weight:0.#}");
                }
            }
        }
        foreach (var child in split.Children)
            CaptureNode(child);
    }
    private static double PositiveGridLengthValue(GridLength length, double fallback)
        => length.Value > 0 ? length.Value : (fallback > 0 ? fallback : 1);
    private void ApplyPaneLayout(PaneNodeSnapshot? snapshot) {
        _zoomedPane = null;
        var built = snapshot is null ? null : BuildFromSnapshot(snapshot, new HashSet<PaneKind>());
        if (built is not null && AllLeaves(built).Any()) {
            _root = built;
            if (!_idePaneApplicable && FindLeaf(PaneKind.Debug) is { Hidden: false } dbg)
                dbg.Hidden = true;
            RebuildPaneLayout();
        } else {
            ApplyDefaultLayout();
        }
    }
    private PaneNode? BuildFromSnapshot(PaneNodeSnapshot snap, HashSet<PaneKind> seen)
        => PaneLayoutTree.BuildFromSnapshot(snap, seen, _paneElements.ContainsKey);
    private static PaneNodeSnapshot ToSnapshot(PaneNode node) => PaneLayoutTree.ToSnapshot(node);
    private static PaneNode? Normalize(PaneNode? node) => PaneLayoutTree.Normalize(node);
}
