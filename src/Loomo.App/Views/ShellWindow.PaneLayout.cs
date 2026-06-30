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
        _paneElements[PaneKind.Debug] = DebugPane;

        // 各コンテンツホスト内の分割マネージャ。タブID→コントロールの解決はワークスペース現在のタブ一覧から行う。
        _editorViews = new PaneSplitView(
            EditorContentHost,
            // ビューポートへ割り当てる時だけ実体化する（resolve は表示対象タブにしか呼ばれない）。
            id => _editorTabs.FirstOrDefault(t => t.Id == id)?.Control,
            // parking 退避は実体化済みのコントロールだけを対象にする（未実体化タブは視覚ツリーに無い）。
            () => _editorTabs.Where(t => t.IsRealized).Select(t => (FrameworkElement)t.Control),
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
            // C# プロジェクトの無いワークスペースでは、保存レイアウトに残る IDE タイルも出さない。
            if (!_idePaneApplicable && FindLeaf(PaneKind.Debug) is { Hidden: false } dbg)
                dbg.Hidden = true;
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

}
