using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペインの表示/非表示トグルと、開いたファイル・結果表示のためのペイン確保 （SetPaneVisible・トグル状態同期・左上入れ替え・最下段追加）。レイアウト構築は ShellWindow.PaneLayout.cs。</summary>
public partial class ShellWindow {
    private void OnHidePane(object sender, RoutedEventArgs e) {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;
        BeginTrailLayoutChange();
        SetPaneVisible(kind, false);
    }
    private const double PaneTogglePopupHoverSlackPx = 120;
    private DispatcherTimer? _paneToggleHoverTimer;
    private void OnMainPaneMouseEnter(object sender, MouseEventArgs e) {
        PaneTogglePopup.IsOpen = true;
        (_paneToggleHoverTimer ??= CreatePaneToggleHoverTimer()).Start();
    }
    private DispatcherTimer CreatePaneToggleHoverTimer() {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => {
            if (!PaneTogglePopup.IsOpen || !IsMouseNearPaneTogglePopup()) {
                PaneTogglePopup.IsOpen = false;
                _paneToggleHoverTimer?.Stop();
            }
        };
        return timer;
    }
    private bool IsMouseNearPaneTogglePopup() {
        if (!GetCursorPos(out var p))
            return true; // 座標取得に失敗したら閉じない（誤クローズより開きっぱなしの方が安全）
        var mouse = new Point(p.X, p.Y);
        return InflatedScreenRect(MainPaneButton).Contains(mouse)
            || (PaneTogglePopupRoot.IsVisible && InflatedScreenRect(PaneTogglePopupRoot).Contains(mouse));
    }
    private static Rect InflatedScreenRect(FrameworkElement element) {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        var rect = new Rect(topLeft, bottomRight);
        rect.Inflate(PaneTogglePopupHoverSlackPx, PaneTogglePopupHoverSlackPx);
        return rect;
    }
    private PaneKind? CurrentMainPane() => _stageActive ? _stagePane : TopLeftPane();
    private static string PaneIconKey(PaneKind kind) => $"PaneIcon.{kind}";
    private void UpdateMainPaneHeader() {
        var main = CurrentMainPane();
        MainPaneIcon.Data = main is { } kind && TryFindResource(PaneIconKey(kind)) is Geometry geo ? geo : null;
        MainPaneButton.ToolTip = main is { } k
            ? $"メインペイン：{PaneLabel(k)}（ホバーで表示ペインを切り替え）"
            : "表示ペインを切り替え（ホバーで一覧）";
    }
    private void OnTogglePaneVisibility(object sender, RoutedEventArgs e) {
        BeginTrailLayoutChange();
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaneKind>(tag, out var kind))
            ToggleSessionEnabled(kind);
        UpdatePaneToggleStates();
    }
    private void UpdatePaneToggleStates() {
        foreach (var child in PaneToggleBar.Children) {
            if (child is not ToggleButton { Tag: string tag } button || !Enum.TryParse<PaneKind>(tag, out var kind))
                continue;
            var enabled = IsSessionEnabled(kind);
            button.IsChecked = enabled;
            button.ToolTip = $"{PaneLabel(kind)} を{(enabled ? "無効化" : "有効化")}";
        }
        UpdateMainPaneHeader();
    }
    private static string PaneLabel(PaneKind kind) => kind switch {
        PaneKind.Terminal => "ターミナル", PaneKind.Editor => "エディタ", PaneKind.EditorSupport => "エディタサポート", PaneKind.Browser => "ブラウザ", PaneKind.Ai => "AI", PaneKind.Git => "Git", PaneKind.Diff => "Diff", PaneKind.Trace => "トレース", PaneKind.Debug => "IDE", _ => kind.ToString(),
    };
    private bool IsPaneVisible(PaneKind kind) => FindLeaf(kind) is { Hidden: false };
    private int VisibleLeafCount() => AllLeaves().Count(l => !l.Hidden);
    private void SetPaneVisible(PaneKind kind, bool visible) {
        var leaf = FindLeaf(kind);
        var currentlyVisible = leaf is { Hidden: false };
        if (visible)
            _enabledSessions.Add(kind);
        if (currentlyVisible == visible)
            return;
        CaptureLayoutSizes();
        if (visible) {
            if (leaf is null) {
                var newLeaf = NewLeaf(kind);
                if (_isSpanMaximized && _root is PaneSplit { Orientation: SplitKind.Columns } columns
                    && columns.Children.Count > 0)
                    columns.Children[^1] = AddLeafAtBottom(columns.Children[^1], newLeaf);
                else
                    AddLeafAtBottom(newLeaf);
            } else
                leaf.Hidden = false;
        } else {
            if (VisibleLeafCount() <= 1)
                return;
            leaf!.Hidden = true;
            if (_focusedRegion?.Pane == kind)
                _focusedRegion = null; // 起点が消えたので次回ナビゲーションは可視ペインから選び直す
        }
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot) {
            if (AllLeaves(savedRoot).FirstOrDefault(l => l.Kind == kind) is { } savedLeaf)
                savedLeaf.Hidden = !visible;
            else if (visible)
                _spanSavedRoot = AddLeafAtBottom(savedRoot, NewLeaf(kind));
        }
        if (kind == PaneKind.EditorSupport && visible)
            _ = UpdateEditorSupportAsync();
        _zoomedPane = null; // 表示構成が変わるのでズームは解除する
        _root = Normalize(_root);
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }
    private void EnsureEditorPaneForOpenedFile(string path) {
        var target = BinaryFileDetector.IsBinary(path) ? PaneKind.EditorSupport : PaneKind.Editor;
        if (_stageActive) {
            if (!OnStage(PaneKind.Editor) && !OnStage(PaneKind.EditorSupport))
                SetStagePane(target);
            return;
        }
        if (IsPaneVisible(PaneKind.Editor) || IsPaneVisible(PaneKind.EditorSupport))
            return;
        PlacePaneByBehavior(target);
    }
    private void EnsurePaneVisibleOrSwapTopLeft(PaneKind target) {
        if (_stageActive) {
            if (!OnStage(target))
                SetStagePane(target);
            return;
        }
        if (IsPaneVisible(target))
            return;
        PlacePaneByBehavior(target);
    }
    private void PlacePaneByBehavior(PaneKind target) {
        switch (_settings.PaneOpenBehavior) {
            case PaneOpenBehavior.Sub:
                PlaceIntoSubPane(target);
                break;
            case PaneOpenBehavior.Loop:
                PlaceIntoLoopPane(target);
                break;
            default:
                SwapIntoTopLeft(target);
                break;
        }
    }
    private void SwapIntoTopLeft(PaneKind target) {
        if (TopLeftPane() is { } topLeft && topLeft != target)
            PlaceWingPane(target, topLeft, center: true, zone: null);
        else
            SetPaneVisible(target, true);
    }
    private void PlaceIntoSubPane(PaneKind target) {
        if (IsPaneVisible(target))
            return;
        var main = TopRowLeftPane();
        var sub = TopRightPane();
        if (sub is { } s && s != main)
            PlaceWingPane(target, s, center: true, zone: null);                // 右上と入れ替え
        else if (main is { } m && m != target)
            PlaceWingPane(target, m, center: false, zone: DropZone.Right);     // 横1枚 → 右に追加
        else
            SetPaneVisible(target, true);
    }
    private void PlaceIntoLoopPane(PaneKind target) {
        if (IsPaneVisible(target))
            return;
        var main = TopRowLeftPane();
        var sub = TopRightPane();
        var originFromSub = _focusedRegion?.Pane is { } origin
            && sub is { } s && s != main && origin == s;
        if (originFromSub && main is { } m && sub is { } current && current != target) {
            PlaceWingPane(current, m, center: true, zone: null);
            PlaceWingPane(target, current, center: false, zone: DropZone.Right);
        } else {
            PlaceIntoSubPane(target);
        }
    }
    private void AddLeafAtBottom(PaneLeaf leaf) => _root = AddLeafAtBottom(_root, leaf);
    private static PaneNode AddLeafAtBottom(PaneNode? root, PaneLeaf leaf) => PaneLayoutTree.AddLeafAtBottom(root, leaf);
}
