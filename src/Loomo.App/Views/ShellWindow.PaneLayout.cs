using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Terminal.Settings;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペインレイアウト（2D並べ替え・ドラッグ移動・ズーム・表示切替・スナップショット適用）</summary>
public partial class ShellWindow
{
    // ===== ペインレイアウト（2D並べ替え・リサイズ・表示切替） =====


    private void InitializePanes()
    {
        _paneElements[PaneKind.Terminal] = TerminalPane;
        _paneElements[PaneKind.Editor] = EditorPane;
        _paneElements[PaneKind.EditorSupport] = EditorSupportPane;
        _paneElements[PaneKind.Browser] = BrowserPane;
        _paneElements[PaneKind.Ai] = AiPane;
        _paneElements[PaneKind.Git] = GitPane;
        _paneElements[PaneKind.Diff] = DiffPane;
        _paneElements[PaneKind.Trace] = TracePane;

        // 各コンテンツホスト内の分割マネージャ。タブID→コントロールの解決はワークスペース現在のタブ一覧から行う。
        _editorViews = new PaneSplitView(
            EditorContentHost,
            id => _editorTabs.FirstOrDefault(t => t.Id == id)?.Control,
            () => _editorTabs.Select(t => (FrameworkElement)t.Control),
            () => (Brush)FindResource("Border"),
            () => (Brush)FindResource("Accent"),
            el => el.Focus(),
            () => SaveActiveWorkspaceSnapshot());
        _terminalViews = new PaneSplitView(
            TerminalContentHost,
            id => _terminalTabs.FirstOrDefault(t => t.Id == id)?.View,
            () => _terminalTabs.Select(t => (FrameworkElement)t.View),
            () => (Brush)FindResource("Border"),
            () => (Brush)FindResource("Accent"),
            el => { if (el is TerminalTabView tv) tv.FocusTerminal(); else el.Focus(); },
            () => SaveActiveWorkspaceSnapshot());
        // レイアウトの構築は OnLoaded（ワークスペース適用 or 既定）に一本化する。
    }

    /// <summary>既定レイアウト：[Editor | (EditorSupport:隠) | Browser] / [Terminal] / [AI]。</summary>
    private void ApplyDefaultLayout()
    {
        _zoomedPane = null;
        var top = new PaneSplit { Orientation = SplitKind.Columns, Weight = 2 };
        top.Children.Add(NewLeaf(PaneKind.Editor));
        // EditorSupport は対応ファイルを開いたとき自動で現れる（位置だけ Editor の右に確保しておく）。
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

    /// <summary>ツリー内のすべてのリーフ（ペイン）を列挙する（実体は <see cref="PaneLayoutTree"/>）。</summary>
    private IEnumerable<PaneLeaf> AllLeaves(PaneNode? node = null) => PaneLayoutTree.AllLeaves(node ?? _root);

    private PaneLeaf? FindLeaf(PaneKind kind) => PaneLayoutTree.FindLeaf(_root, kind);

    /// <summary>指定ノードを直接の子に持つスプリットを返す（ルートなら null）。</summary>
    private PaneSplit? FindParent(PaneNode target, PaneNode? current = null)
        => PaneLayoutTree.FindParent(current ?? _root, target);

    /// <summary>
    /// 現在の <see cref="_root"/>（レイアウトツリー）に合わせて <c>PaneHost</c> を組み直す。
    /// スプリットごとに Grid を生成し、ペイン本体はその Grid へ再ペアレントする
    /// （同一ウィンドウ内のため WebView2 等は生存する）。
    /// </summary>
    private void RebuildPaneLayout()
    {
        // ステージモード中はタイルを組まずステージ側を組み直す（SetPaneVisible 等の
        // 既存フローがそのまま流れてきても表示が壊れないように）。ツリーへの変更は
        // 反映済みなので、ステージ解除時の再構築で通常レイアウトにも現れる。
        if (_stageActive)
        {
            RebuildStage();
            return;
        }

        // すべてのペインを現在の親から外してからホストを作り直す
        foreach (var element in _paneElements.Values)
            if (element.Parent is Panel parent)
                parent.Children.Remove(element);

        PaneHost.Children.Clear();
        PaneHost.RowDefinitions.Clear();
        PaneHost.ColumnDefinitions.Clear();

        _root = Normalize(_root);
        if (_root is null)
        {
            ApplyDefaultLayout();
            return;
        }

        // 不変条件：Main（タイル）に出ている（非 Hidden の）リーフは必ず有効扱いにする。
        // レイアウト切替や旧データ移行で、Main に出ているのにトグルが消灯…という不整合を防ぐ。
        foreach (var leaf in AllLeaves())
            if (!leaf.Hidden)
                _enabledSessions.Add(leaf.Kind);

        UpdatePaneToggleStates();

        // ズーム中はツリーを保ったまま、対象ペイン1枚だけを全面表示する。
        if (_zoomedPane is { } zoom)
        {
            if (FindLeaf(zoom) is { Hidden: false } && _paneElements.TryGetValue(zoom, out var zoomElement))
            {
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
        if (visual is null)
        {
            // 可視ペインが1枚も無い（理論上は起きない）場合は既定へ戻す。
            ApplyDefaultLayout();
            return;
        }
        PaneHost.Children.Add(visual);

        // Browser ペインが（再）表示されたら、遅延していたアクティブタブの WebView2 を実体化する。
        ScheduleBrowserRealize(_activeBrowserTab);
        // 袖（ミニチュア）はレイアウトモードでも常設。レイアウト確定後にライブ実体から組み直す。
        ScheduleLayoutWings();
    }

    /// <summary>
    /// 1ノード分のビジュアルを生成する（リーフ＝ペイン本体、スプリット＝Grid）。
    /// 非表示リーフは描画せず、可視な子だけでトラックを組む。すべて非表示なら null。
    /// </summary>
    private FrameworkElement? BuildNode(PaneNode node, Brush border)
    {
        if (node is PaneLeaf leaf)
        {
            if (leaf.Hidden)
                return null;
            var element = _paneElements[leaf.Kind];
            element.Visibility = Visibility.Visible;
            return element;
        }

        var split = (PaneSplit)node;
        // 描画しないノードのサイズ取り込みを防ぐため、毎回リセットしてから割り当て直す。
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

        for (var i = 0; i < visibleChildren.Count; i++)
        {
            if (i > 0)
            {
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

    /// <summary>ノード（リーフ／スプリット）に可視なペインが含まれるか。</summary>
    private static bool IsNodeVisible(PaneNode node) => PaneLayoutTree.IsNodeVisible(node);

    private static void AddTrack(Grid grid, bool cols, GridLength length, double min = 0)
    {
        if (cols)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length, MinWidth = min });
        else
            grid.RowDefinitions.Add(new RowDefinition { Height = length, MinHeight = min });
    }

    private static void SetTrack(UIElement element, bool cols, int index)
    {
        if (cols)
            Grid.SetColumn(element, index);
        else
            Grid.SetRow(element, index);
    }

    private GridSplitter NewSplitter(bool cols, Brush border, PaneSplit split)
    {
        var accent = (Brush)FindResource("Accent");
        var splitter = new GridSplitter
        {
            Width = cols ? SplitterThickness : double.NaN,
            Height = cols ? double.NaN : SplitterThickness,
            ResizeDirection = cols ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = border,
            Cursor = cols ? Cursors.SizeWE : Cursors.SizeNS,
            ToolTip = "ドラッグでリサイズ／ダブルクリックで均等化"
        };
        // ホバーでアクセント色に光らせ、「ここを掴める」ことを明示する。
        splitter.MouseEnter += (_, _) => splitter.Background = accent;
        splitter.MouseLeave += (_, _) => splitter.Background = border;
        splitter.DragCompleted += (_, _) => { MarkLayoutDirty(); SaveActiveWorkspaceSnapshot(); };
        splitter.MouseDoubleClick += (_, e) => { EqualizeSiblings(split); e.Handled = true; };
        return splitter;
    }

    /// <summary>スプリッターのダブルクリックで、その分割直下の可視ペインの比率を均等に戻す。</summary>
    private void EqualizeSiblings(PaneSplit split)
    {
        foreach (var child in split.Children)
            child.Weight = 1;
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>レイアウトモードでタイル配置を編集したら、現在の保存レイアウトから「未保存」へ印を付ける
    /// （次の Ctrl+T 巡回でスクラッチへ退避される）。ソロモードでは何もしない。</summary>
    private void MarkLayoutDirty()
    {
        if (_stageActive || _layoutDirty)
            return;
        _layoutDirty = true;
        UpdateModeButtons();
    }

    /// <summary>フォーカス中（無ければ最初の可視）ペインのズームをトグルする。
    /// ステージモード中は「全面表示」の意味を俯瞰へ読み替える。</summary>
    private void ToggleZoom()
    {
        if (_stageActive)
        {
            ToggleOverview();
            return;
        }
        if (_zoomedPane is not null)
        {
            ZoomPane(null);
            return;
        }
        var target = _focusedRegion?.Pane ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind;
        if (target is { } kind)
            ZoomPane(kind);
    }

    /// <summary>指定ペインのズームをトグルする（タイトルのダブルクリック用）。</summary>
    private void ToggleZoomFor(PaneKind kind) => ZoomPane(_zoomedPane == kind ? null : kind);

    /// <summary>
    /// Ctrl+W x：フォーカス中の領域を隠す。サイドバーにフォーカスがあればサイドバーを閉じ、
    /// ペインにフォーカスがあれば（無ければ最初の可視ペインを）非表示にする。
    /// 隠したペインはタイトルバーの表示トグルから戻せる（<see cref="SetPaneVisible"/> 参照）。
    /// </summary>
    private void HideFocusedRegion()
    {
        if (_focusedRegion is { IsSidebar: true })
        {
            _vm.IsSidebarVisible = false;
            return;
        }
        // ステージモード中はペインを「隠す」概念が無い（全員が舞台か袖にいる）。
        if (_stageActive)
            return;
        var target = _focusedRegion?.Pane ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind;
        if (target is { } kind)
            SetPaneVisible(kind, false);
    }

    /// <summary>
    /// ペインを一時的に全面表示する／解除する。ツリーは保持するので、解除すれば元の配置・比率へ戻る。
    /// ズームに入る前に現在の比率を取り込んでおき、復元時に崩れないようにする。
    /// </summary>
    private void ZoomPane(PaneKind? kind)
    {
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

    /// <summary>GridSplitter 操作後の行高・列幅（star比率）を現在のツリーへ取り込む。</summary>
    private void CaptureLayoutSizes() => CaptureNode(_root);

    private static void CaptureNode(PaneNode? node)
    {
        if (node is not PaneSplit split)
            return;

        if (split.Host is { } grid)
        {
            var cols = split.Orientation == SplitKind.Columns;
            foreach (var child in split.Children)
            {
                // 描画された可視な子だけが実トラック（BuildNode が設定）を持つ。
                var index = child.TrackIndex;
                if (index < 0)
                    continue;
                if (cols)
                {
                    if (index < grid.ColumnDefinitions.Count)
                    {
                        var definition = grid.ColumnDefinitions[index];
                        child.Weight = definition.ActualWidth > 0
                            ? definition.ActualWidth
                            : PositiveGridLengthValue(definition.Width, child.Weight);
                    }
                }
                else
                {
                    if (index < grid.RowDefinitions.Count)
                    {
                        var definition = grid.RowDefinitions[index];
                        child.Weight = definition.ActualHeight > 0
                            ? definition.ActualHeight
                            : PositiveGridLengthValue(definition.Height, child.Weight);
                    }
                }
            }
        }

        foreach (var child in split.Children)
            CaptureNode(child);
    }

    private static double PositiveGridLengthValue(GridLength length, double fallback)
        => length.Value > 0 ? length.Value : (fallback > 0 ? fallback : 1);

    /// <summary>保存済みレイアウトを適用する。非表示ペインはリーフの Hidden で復元する。</summary>
    private void ApplyPaneLayout(PaneNodeSnapshot? snapshot)
    {
        _zoomedPane = null;
        var built = snapshot is null ? null : BuildFromSnapshot(snapshot, new HashSet<PaneKind>());
        if (built is not null && AllLeaves(built).Any())
        {
            _root = built;
            RebuildPaneLayout();
        }
        else
        {
            ApplyDefaultLayout();
        }
    }

    /// <summary>スナップショットからツリーを再構築する（重複・未知のペインは捨てる）。</summary>
    private PaneNode? BuildFromSnapshot(PaneNodeSnapshot snap, HashSet<PaneKind> seen)
        => PaneLayoutTree.BuildFromSnapshot(snap, seen, _paneElements.ContainsKey);

    private static PaneNodeSnapshot ToSnapshot(PaneNode node) => PaneLayoutTree.ToSnapshot(node);

    /// <summary>
    /// ツリーを正規化する：空スプリットを除去し、子が1つのスプリットを畳み、
    /// 同方向に入れ子になったスプリットをフラット化する。
    /// </summary>
    private static PaneNode? Normalize(PaneNode? node) => PaneLayoutTree.Normalize(node);

    private void OnPaneTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        // ソロモード中はタイル前提の操作（ドラッグ移動・ダブルクリックズーム）を無効化する（舞台は1枚）。
        if (_stageActive)
            return;
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;

        // ダブルクリックでそのペインをズーム／復元する（tmux の zoom 相当）。
        // ただしタブや操作ボタンの上では割り込まない（タブの2連クリックやボタンの
        // ダブルクリックを横取りしないため）。
        if (e.ClickCount == 2)
        {
            if (IsWithinButton(e.OriginalSource))
                return;
            ToggleZoomFor(kind);
            e.Handled = true;
            return;
        }

        // ここでは捕捉しない（下にあるタブ／ボタンのクリックを殺さないため）。開始位置だけ控え、
        // しきい値を超えて動いたときに初めて OnPaneTitleMouseMove がドラッグを開始する。
        // Preview（トンネル）で拾うので、タブ・ボタンの上から掴んでもヘッダー全域でドラッグできる。
        _paneDragStart = e.GetPosition(null);
        _paneDragArmed = true;
    }

    private void OnPaneTitleMouseMove(object sender, MouseEventArgs e)
    {
        // ソロモード中はタイル前提の並べ替えドラッグを無効化する。
        if (_stageActive)
            return;
        if (_paneDragging || !_paneDragArmed)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            DisarmTitleDrag();
            return;
        }

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _paneDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _paneDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaneKind>(tag, out var kind))
        {
            // しきい値超え。BeginPaneDrag がオーバーレイへ捕捉を移すので、タブ／ボタンが
            // 押下時に握った捕捉も奪われ、ドラッグへ切り替わる（＝そのクリックは成立しない）。
            DisarmTitleDrag();
            BeginPaneDrag(kind);
        }
    }

    private void OnPaneTitleMouseUp(object sender, MouseButtonEventArgs e)
    {
        DisarmTitleDrag();
    }

    /// <summary>ドラッグ判定を解除する。</summary>
    private void DisarmTitleDrag()
    {
        _paneDragArmed = false;
        if (_dragHandle is not null)
        {
            if (ReferenceEquals(Mouse.Captured, _dragHandle))
                _dragHandle.ReleaseMouseCapture();
            _dragHandle = null;
        }
    }

    /// <summary>ヒットテストの起点要素が（ツリーを遡って）ボタンの内側にあるか。</summary>
    private static bool IsWithinButton(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
                return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>
    /// スナップ風のレイアウト・ドラッグを開始する。ドラッグ中は PaneHost と同セルの透明オーバーレイ
    /// （<c>PaneDragOverlay</c>）を被せ、その上でマウスを追跡＆プレビューを描画する。
    /// Popup ではなくウィンドウ内レイヤなのは、Popup が1モニタへクリップされて
    /// マルチモニタ跨ぎ最大化中に隣のモニタでプレビューが見えなくなるため。
    /// </summary>
    private void BeginPaneDrag(PaneKind source)
    {
        if (_zoomedPane is not null)
            return; // ズーム中は移動先が1枚しか見えないので並べ替えしない
        if (VisibleLeafCount() <= 1)
            return; // 1枚だけなら移動先がない

        EnsureDragOverlay();
        _dragSource = source;
        _dragTarget = null;
        _dragZone = null;
        _dragFromWing = false;
        _dragCenter = false;
        _dragSpan = false;
        _paneDragging = true;

        _dragPreview!.Visibility = Visibility.Collapsed;
        _dragTargetOutline!.Visibility = Visibility.Collapsed;
        PaneDragOverlay.Visibility = Visibility.Visible;
        ShowDragGhost(source);
        MoveDragGhost(Mouse.GetPosition(DragGhostLayer));

        // 表示直後で捕捉に失敗した場合は次の入力タイミングで再試行する。
        if (!Mouse.Capture(_dragCanvas, CaptureMode.SubTree))
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_paneDragging)
                        Mouse.Capture(_dragCanvas, CaptureMode.SubTree);
                }),
                System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>袖（ミニチュア）からのドラッグを開始する。ドロップ先のタイル上では既存のゾーン
    /// プレビューを出し、中央なら入れ替え・端なら分割挿入する（<see cref="PlaceWingPane"/>）。
    /// レイアウトモード専用（ステージ中はタイルが無いので無効）。</summary>
    private void BeginWingDrag(PaneKind source)
    {
        if (_stageActive || VisibleLeafCount() < 1)
            return;

        EnsureDragOverlay();
        _dragSource = source;
        _dragTarget = null;
        _dragZone = null;
        _dragFromWing = true;
        _dragCenter = false;
        _dragSpan = false;
        _paneDragging = true;

        _dragPreview!.Visibility = Visibility.Collapsed;
        _dragTargetOutline!.Visibility = Visibility.Collapsed;
        PaneDragOverlay.Visibility = Visibility.Visible;
        ShowDragGhost(source);
        MoveDragGhost(Mouse.GetPosition(DragGhostLayer));

        if (!Mouse.Capture(_dragCanvas, CaptureMode.SubTree))
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (_paneDragging)
                        Mouse.Capture(_dragCanvas, CaptureMode.SubTree);
                }),
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EnsureDragOverlay()
    {
        if (_dragCanvas is not null)
            return;

        var accent = (Brush)FindResource("Accent");
        _dragTargetOutline = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Background = MakeTranslucent(accent, 0.10),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _dragPreview = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(2),
            Background = MakeTranslucent(accent, 0.35),
            CornerRadius = new CornerRadius(2),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        // PaneDragOverlay は PaneHost と同セルなので、Canvas 上の座標＝PaneHost 座標になる。
        // ClipToBounds でタイル領域外（右の袖＝ミニチュア列など）へプレビューがはみ出さないようにする。
        _dragCanvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        _dragCanvas.Children.Add(_dragTargetOutline);
        _dragCanvas.Children.Add(_dragPreview);
        _dragCanvas.MouseMove += OnDragCanvasMouseMove;
        _dragCanvas.MouseLeftButtonUp += OnDragCanvasMouseUp;
        _dragCanvas.LostMouseCapture += OnDragCanvasLostCapture;
        PaneDragOverlay.Children.Add(_dragCanvas);
    }

    private void OnDragCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_paneDragging)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // ボタンアップを取りこぼした場合の保険
            EndPaneDrag();
            return;
        }

        MoveDragGhost(e.GetPosition(DragGhostLayer));
        UpdateDragPreview(e.GetPosition(PaneHost));
    }

    private void UpdateDragPreview(Point pos)
    {
        var hit = HitTestCell(pos);
        if (hit is null)
        {
            // タイル外（袖のミニチュアや余白の上）はドロップ無効＝レイアウトは変わらない。
            // 変更しそうなプレビューは出さず、カーソルも禁止にして「ここには落とせない」を示す。
            _dragTarget = null;
            _dragZone = null;
            _dragCenter = false;
            _dragSpan = false;
            _dragPreview!.Visibility = Visibility.Collapsed;
            _dragTargetOutline!.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor = Cursors.No;
            return;
        }
        Mouse.OverrideCursor = Cursors.Hand;

        var (kind, rect) = hit.Value;
        var relX = rect.Width > 0 ? (pos.X - rect.X) / rect.Width : 0.5;
        var relY = rect.Height > 0 ? (pos.Y - rect.Y) / rect.Height : 0.5;
        var zone = NearestZone(relX, relY);
        // 袖からのドラッグはセル中央に「入れ替え」ゾーンを設ける（端は従来どおり分割挿入）。
        var center = _dragFromWing && relX is > 0.34 and < 0.66 && relY is > 0.34 and < 0.66;

        // セルの外縁ぎりぎり（当該辺へ寄せきった位置）では、単体ペインではなく直交する祖先スプリット
        // 全体の辺へ落とす「スパン挿入」を提示する（例：左右2ペインの下端でフル幅プレビュー）。
        var span = !center && IsNearOuterEdge(relX, relY, zone)
            && TryGetSpanRect(kind, zone, out var spanRect) && SpanAddsBreadth(spanRect, rect, zone);
        var outlineRect = span ? spanRect : rect;
        var previewRect = center ? rect : ZoneRect(span ? spanRect : rect, zone);

        _dragTarget = kind;
        _dragZone = zone;
        _dragCenter = center;
        _dragSpan = span;

        PlaceOverlay(_dragTargetOutline!, outlineRect);
        PlaceOverlay(_dragPreview!, previewRect);
        _dragTargetOutline!.Visibility = Visibility.Visible;
        _dragPreview!.Visibility = Visibility.Visible;
    }

    /// <summary>当該辺に十分寄せきっているか（外縁から内側 20% のバンド）。スパン挿入の発火条件。</summary>
    private static bool IsNearOuterEdge(double relX, double relY, DropZone zone) => zone switch
    {
        DropZone.Left => relX < 0.2,
        DropZone.Right => relX > 0.8,
        DropZone.Above => relY < 0.2,
        _ => relY > 0.8,
    };

    /// <summary>ターゲットペインを起点に、<paramref name="zone"/> 方向へ伸ばせる祖先スプリットの矩形
    /// （PaneHost 座標、配下の可視リーフの和）を返す。祖先が無ければ false。</summary>
    private bool TryGetSpanRect(PaneKind targetKind, DropZone zone, out Rect rect)
    {
        rect = default;
        if (FindLeaf(targetKind) is not { } targetLeaf)
            return false;
        var node = PaneLayoutTree.ResolveSpanTarget(_root, targetLeaf, zone);
        if (ReferenceEquals(node, targetLeaf))
            return false; // 直交する祖先が無い＝単体ペインへの挿入と同じ

        var any = false;
        foreach (var leaf in AllLeaves(node))
        {
            if (leaf.Hidden || !TryGetPaneRect(leaf.Kind, out var r))
                continue;
            rect = any ? Rect.Union(rect, r) : r;
            any = true;
        }
        return any;
    }

    /// <summary>スパン矩形が単体ペインより当該辺方向に広いか（同寸ならスパンの意味が無いので出さない）。</summary>
    private static bool SpanAddsBreadth(Rect span, Rect leaf, DropZone zone)
        => zone is DropZone.Above or DropZone.Below
            ? span.Width > leaf.Width + 1
            : span.Height > leaf.Height + 1;

    private static void PlaceOverlay(Border border, Rect r)
    {
        Canvas.SetLeft(border, r.X);
        Canvas.SetTop(border, r.Y);
        border.Width = r.Width;
        border.Height = r.Height;
    }

    private void OnDragCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        var source = _dragSource;
        var target = _dragTarget;
        var zone = _dragZone;
        var center = _dragCenter;
        var span = _dragSpan;
        var fromWing = _dragFromWing;
        EndPaneDrag();

        if (target is not { } t || t == source)
            return;
        if (fromWing)
            PlaceWingPane(source, t, center, zone, span);
        else if (zone is { } z)
            MovePane(source, t, z, span);
    }

    private void OnDragCanvasLostCapture(object sender, MouseEventArgs e)
    {
        if (_paneDragging)
            EndPaneDrag();
    }

    /// <summary>ドラッグ中のゴースト（掴んでいるペイン名のチップ）を出し、カーソル追従させる。
    /// ドラッグが始まったことを一目で分かるようにするための手応え。</summary>
    private void ShowDragGhost(PaneKind kind)
    {
        if (_dragGhost is null)
        {
            _dragGhost = new Border
            {
                Background = MakeTranslucent((Brush)FindResource("Accent"), 0.9),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 5, 10, 5),
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 8, ShadowDepth = 2, Opacity = 0.5, Color = Colors.Black
                },
                Child = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold }
            };
            DragGhostLayer.Children.Add(_dragGhost);
        }
        ((TextBlock)_dragGhost.Child).Text = PaneLabel(kind);
        _dragGhost.Visibility = Visibility.Visible;
        DragGhostLayer.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.Hand;   // 掴んでいる感じ（タイル外では UpdateDragPreview が No へ）
    }

    /// <summary>ゴーストをカーソル位置（DragGhostLayer 座標）へ追従させる。少しずらしてポインタを隠さない。</summary>
    private void MoveDragGhost(Point pos)
    {
        if (_dragGhost is null)
            return;
        Canvas.SetLeft(_dragGhost, pos.X + 14);
        Canvas.SetTop(_dragGhost, pos.Y + 16);
    }

    private void HideDragGhost()
    {
        if (_dragGhost is not null)
            _dragGhost.Visibility = Visibility.Collapsed;
        DragGhostLayer.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
    }

    private void EndPaneDrag()
    {
        _paneDragging = false;
        _dragFromWing = false;
        _dragCenter = false;
        _dragSpan = false;
        HideDragGhost();
        if (ReferenceEquals(Mouse.Captured, _dragCanvas))
            Mouse.Capture(null);
        PaneDragOverlay.Visibility = Visibility.Collapsed;
        if (_dragPreview is not null)
            _dragPreview.Visibility = Visibility.Collapsed;
        if (_dragTargetOutline is not null)
            _dragTargetOutline.Visibility = Visibility.Collapsed;
    }

    /// <summary>カーソル位置にあるペインのセルとその矩形（PaneHost 座標）を返す。
    /// 非表示（袖＝ミニチュア送り）のリーフは除外する。これらの実体は袖の描画元として
    /// StageSourceArea（PaneHost と同じ列・全面サイズ）に生きており、含めるとタイルの隙間などで
    /// 全面サイズの矩形にヒットしてしまい、ミニチュア宛ての巨大なプレビューが出てしまう。</summary>
    private (PaneKind Kind, Rect Rect)? HitTestCell(Point pos)
    {
        foreach (var leaf in AllLeaves())
        {
            if (leaf.Hidden)
                continue;
            if (TryGetPaneRect(leaf.Kind, out var rect) && rect.Contains(pos))
                return (leaf.Kind, rect);
        }
        return null;
    }

    /// <summary>セル内の相対位置から最も近い辺（=ドロップ先）を求める。</summary>
    private static DropZone NearestZone(double relX, double relY)
    {
        var dLeft = relX;
        var dRight = 1 - relX;
        var dTop = relY;
        var dBottom = 1 - relY;
        var min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));
        if (min == dLeft) return DropZone.Left;
        if (min == dRight) return DropZone.Right;
        if (min == dTop) return DropZone.Above;
        return DropZone.Below;
    }

    private static Rect ZoneRect(Rect r, DropZone zone) => zone switch
    {
        DropZone.Left => new Rect(r.X, r.Y, r.Width / 2, r.Height),
        DropZone.Right => new Rect(r.X + r.Width / 2, r.Y, r.Width / 2, r.Height),
        DropZone.Above => new Rect(r.X, r.Y, r.Width, r.Height / 2),
        _ => new Rect(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2),
    };

    private static Brush MakeTranslucent(Brush source, double opacity)
    {
        if (source is SolidColorBrush solid)
        {
            var c = solid.Color;
            return new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), c.R, c.G, c.B));
        }

        var clone = source.Clone();
        clone.Opacity = opacity;
        return clone;
    }

    /// <summary>ペインを移動する。<paramref name="span"/> なら単体ペインでなく、ターゲットの当該辺を
    /// 端まで占めるスプリット全体の辺へ落とす（例：左右2ペインの下へフル幅で挿入）。</summary>
    private void MovePane(PaneKind source, PaneKind target, DropZone zone, bool span = false)
    {
        if (source == target)
            return;

        var sourceLeaf = FindLeaf(source);
        var targetLeaf = FindLeaf(target);
        if (sourceLeaf is null || targetLeaf is null)
            return;

        CaptureLayoutSizes();

        // 移動元をツリーから外し、ターゲット（またはスパン対象の祖先）の指定した辺へ挿入する。
        _root = RemoveNode(_root, sourceLeaf);
        sourceLeaf.Weight = 1;
        var insertTarget = span ? PaneLayoutTree.ResolveSpanTarget(_root, targetLeaf, zone) : targetLeaf;
        _root = InsertRelative(_root, sourceLeaf, insertTarget, zone);

        // 跨ぎ最大化中の移動は、解除時に戻す保存レイアウトへも同じ論理操作を反映する
        // （解除やスナップショット保存で移動が巻き戻らないように）。
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
            _spanSavedRoot = MoveInTree(savedRoot, source, target, zone, span);

        _root = Normalize(_root);
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>指定ツリー上で <see cref="MovePane"/> と同じ移動を行い、新しいルートを返す。</summary>
    private static PaneNode? MoveInTree(PaneNode root, PaneKind source, PaneKind target, DropZone zone, bool span = false)
        => PaneLayoutTree.MoveInTree(root, source, target, zone, span);

    /// <summary>袖（ミニチュア）のペインをタイルへ配置する。<paramref name="center"/> なら入れ替え
    /// （ターゲットの位置へ据え、元のペインは袖へ退場）、それ以外は <paramref name="zone"/> の辺へ分割挿入する。</summary>
    private void PlaceWingPane(PaneKind dragged, PaneKind target, bool center, DropZone? zone, bool span = false)
    {
        if (dragged == target || FindLeaf(target) is null)
            return;

        CaptureLayoutSizes();
        _enabledSessions.Add(dragged);   // タイルに出る＝有効

        _root = PlaceInTree(_root, dragged, target, center, zone, span);
        // 跨ぎ最大化中は、解除時に戻す保存レイアウトへも同じ配置を反映する。
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
            _spanSavedRoot = PlaceInTree(savedRoot, dragged, target, center, zone, span);

        MarkLayoutDirty();
        RebuildPaneLayout();
        FocusPane(dragged);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>ツリーへ <paramref name="dragged"/> を配置した新しいルートを返す。入れ替えなら
    /// ターゲットの位置へ据えてターゲットをツリーから外し（＝袖へ）、挿入ならターゲットの指定辺へ分割する。</summary>
    private static PaneNode? PlaceInTree(PaneNode? root, PaneKind dragged, PaneKind target, bool center, DropZone? zone, bool span = false)
    {
        if (root is null)
            return root;
        var targetLeaf = PaneLayoutTree.FindLeaf(root, target);
        if (targetLeaf is null)
            return root;

        // 既にツリーに在る（隠れているなど）なら取り外して、配置先を一意にする。
        if (PaneLayoutTree.FindLeaf(root, dragged) is { } existing)
            root = RemoveNode(root, existing);

        if (center)
        {
            // 入れ替え：ターゲットの隣へ同じ比率で挿し、ターゲットを外す＝ターゲットの場所を引き継ぐ。
            var leaf = new PaneLeaf { Kind = dragged, Weight = targetLeaf.Weight };
            root = InsertRelative(root, leaf, targetLeaf, DropZone.Left);
            root = RemoveNode(root, targetLeaf);
        }
        else
        {
            var z = zone ?? DropZone.Right;
            // スパン挿入なら単体ペインでなく、当該辺を端まで占める祖先スプリットへ落とす。
            PaneNode insertTarget = span ? PaneLayoutTree.ResolveSpanTarget(root, targetLeaf, z) : targetLeaf;
            root = InsertRelative(root, new PaneLeaf { Kind = dragged }, insertTarget, z);
        }
        return Normalize(root);
    }

    /// <summary>ノードを親スプリットから取り外し、新しいルートを返す（畳み込みは Normalize に任せる）。</summary>
    private static PaneNode? RemoveNode(PaneNode? root, PaneNode node) => PaneLayoutTree.RemoveNode(root, node);

    /// <summary>
    /// <paramref name="node"/> を <paramref name="target"/> の指定した辺へ挿入し、新しいルートを返す
    /// （実体は <see cref="PaneLayoutTree.InsertRelative"/>）。
    /// </summary>
    private static PaneNode? InsertRelative(PaneNode? root, PaneNode node, PaneNode target, DropZone zone)
        => PaneLayoutTree.InsertRelative(root, node, target, zone);

    private void OnHidePane(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<PaneKind>(tag, out var kind))
            return;
        SetPaneVisible(kind, false);
    }

    private void OnTogglePaneVisibility(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaneKind>(tag, out var kind))
            ToggleSessionEnabled(kind);

        // ToggleSessionEnabled が何もしなかった場合（最後の1枚は無効化できない等）も、クリックで
        // 勝手に反転した IsChecked を実状態へ戻す必要があるためここでも同期する。
        UpdatePaneToggleStates();
    }

    /// <summary>
    /// タイトルバーのペイントグルを実際の有効状態へ同期する（IsChecked＝有効→アクセント色）。
    /// ツールチップも「有効化／無効化」を状態に合わせて切り替える。
    /// </summary>
    private void UpdatePaneToggleStates()
    {
        foreach (var child in PaneToggleBar.Children)
        {
            if (child is not ToggleButton { Tag: string tag } button || !Enum.TryParse<PaneKind>(tag, out var kind))
                continue;
            var enabled = IsSessionEnabled(kind);
            button.IsChecked = enabled;
            button.ToolTip = $"{PaneLabel(kind)} を{(enabled ? "無効化" : "有効化")}";
        }
    }

    /// <summary>ペインの日本語表示名（ペイントグルのツールチップ用）。</summary>
    private static string PaneLabel(PaneKind kind) => kind switch
    {
        PaneKind.Terminal => "ターミナル",
        PaneKind.Editor => "エディタ",
        PaneKind.EditorSupport => "エディタサポート",
        PaneKind.Browser => "ブラウザ",
        PaneKind.Ai => "AI",
        PaneKind.Git => "Git",
        PaneKind.Diff => "Diff",
        PaneKind.Trace => "トレース",
        _ => kind.ToString(),
    };

    /// <summary>ペインがツリーに在りかつ表示中か。</summary>
    private bool IsPaneVisible(PaneKind kind) => FindLeaf(kind) is { Hidden: false };

    /// <summary>表示中（非 Hidden）のリーフ数。</summary>
    private int VisibleLeafCount() => AllLeaves().Count(l => !l.Hidden);

    /// <summary>
    /// ペインの表示／非表示を切り替える。非表示にしてもリーフはツリーに残し
    /// <see cref="PaneLeaf.Hidden"/> を立てるだけなので、再表示で元の位置・比率に戻る。
    /// </summary>
    private void SetPaneVisible(PaneKind kind, bool visible)
    {
        var leaf = FindLeaf(kind);
        var currentlyVisible = leaf is { Hidden: false };

        // Main に出るペインは必ず有効扱いにする（トグル以外の自動表示＝EditorSupport・ターミナル
        // セット等から呼ばれても「Main に出ている＝無効」という不整合を生まない）。隠す側では
        // 有効状態は変えない（隠れた有効セッションは袖へ回る）。
        if (visible)
            _enabledSessions.Add(kind);

        if (currentlyVisible == visible)
            return;

        CaptureLayoutSizes();

        if (visible)
        {
            if (leaf is null)
            {
                // 一度もツリーに置かれていないペイン。跨ぎ最大化中はモニタの継ぎ目を跨ぐ
                // 全幅の行ではなく、右端の列の最下段へ入れる。
                var newLeaf = NewLeaf(kind);
                if (_isSpanMaximized && _root is PaneSplit { Orientation: SplitKind.Columns } columns
                    && columns.Children.Count > 0)
                    columns.Children[^1] = AddLeafAtBottom(columns.Children[^1], newLeaf);
                else
                    AddLeafAtBottom(newLeaf);
            }
            else
                leaf.Hidden = false;
        }
        else
        {
            // 最後の1枚は隠さない
            if (VisibleLeafCount() <= 1)
                return;
            leaf!.Hidden = true;
            if (_focusedRegion?.Pane == kind)
                _focusedRegion = null; // 起点が消えたので次回ナビゲーションは可視ペインから選び直す
        }

        // 跨ぎ最大化中の表示切替は、解除時に戻す保存レイアウトへも反映する
        // （跨ぎ解除やスナップショット保存で表示状態が巻き戻らないように）。
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
        {
            if (AllLeaves(savedRoot).FirstOrDefault(l => l.Kind == kind) is { } savedLeaf)
                savedLeaf.Hidden = !visible;
            else if (visible)
                _spanSavedRoot = AddLeafAtBottom(savedRoot, NewLeaf(kind));
        }

        // EditorSupport を表示にしたら、現在のエディタ内容でプレビューを流し込む（自動開閉はしない）。
        if (kind == PaneKind.EditorSupport && visible)
            _ = UpdateEditorSupportAsync();

        _zoomedPane = null; // 表示構成が変わるのでズームは解除する
        _root = Normalize(_root);
        MarkLayoutDirty();
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>
    /// ファイルを開くとき、Editor も EditorSupport もレイアウトに出ていなければ、左上のペインを
    /// 開く対象（バイナリ＝EditorSupport／テキスト＝Editor）へ差し替えて必ず見えるようにする。
    /// FolderTree・Diff・Git・検索・ターミナル/エディタのリンクなど、すべての「ファイルを開く」経路の
    /// 共通前処理として、呼び出し側がタブを活性化する前に呼ぶ。どちらかが既に見えていれば何もしない。
    /// </summary>
    private void EnsureEditorPaneForOpenedFile(string path)
    {
        var target = BinaryFileDetector.IsBinary(path) ? PaneKind.EditorSupport : PaneKind.Editor;

        if (_stageActive)
        {
            // ソロモード：Editor も EditorSupport も舞台に立っていなければ、対象を舞台へ立てる。
            if (!OnStage(PaneKind.Editor) && !OnStage(PaneKind.EditorSupport))
                SetStagePane(target);
            return;
        }

        // タイルモード：どちらかが表示中なら現状維持。両方とも非表示のときだけ左上を差し替える。
        if (IsPaneVisible(PaneKind.Editor) || IsPaneVisible(PaneKind.EditorSupport))
            return;

        // 左上の可視ペインを対象へ入れ替える（元の左上ペインは袖へ退場）。左上が取れない/対象自身が
        // 左上のときは、対象を素直に表示する。
        if (TopLeftPane() is { } topLeft && topLeft != target)
            PlaceWingPane(target, topLeft, center: true, zone: null);
        else
            SetPaneVisible(target, true);
    }

    /// <summary>
    /// 指定ペインがレイアウトに出ていなければ、左上のペインと入れ替えて必ず見えるようにする
    /// （元の左上ペインは袖へ退場）。ステージモード中は対象を舞台へ立てる。既に見えていれば何もしない。
    /// 「AIに聞く」「ブラウザで調べる」のように、結果を表示するペインを前面に出す経路で使う。
    /// </summary>
    private void EnsurePaneVisibleOrSwapTopLeft(PaneKind target)
    {
        if (_stageActive)
        {
            if (!OnStage(target))
                SetStagePane(target);
            return;
        }

        if (IsPaneVisible(target))
            return;

        if (TopLeftPane() is { } topLeft && topLeft != target)
            PlaceWingPane(target, topLeft, center: true, zone: null);
        else
            SetPaneVisible(target, true);
    }

    /// <summary>再表示するペインを最下段の新しい行として追加する。</summary>
    private void AddLeafAtBottom(PaneLeaf leaf) => _root = AddLeafAtBottom(_root, leaf);

    /// <summary>指定ツリーの最下段の新しい行としてリーフを追加し、新しいルートを返す。
    /// 既存ノードを行スプリットで包む場合は外側の重み（親スプリット内の比率）を引き継ぐ。</summary>
    private static PaneNode AddLeafAtBottom(PaneNode? root, PaneLeaf leaf) => PaneLayoutTree.AddLeafAtBottom(root, leaf);
}
