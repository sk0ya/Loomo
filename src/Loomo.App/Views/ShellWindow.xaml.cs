using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow : Window
{
    private readonly TerminalService _terminal;
    private readonly EditorService _editor;
    private readonly IWorkspaceService _workspace;
    private readonly TabIconService _tabIcons;
    private readonly ShellViewModel _vm;
    private readonly Dictionary<Guid, TerminalWorkspaceTabs> _terminalWorkspaces = new();
    private readonly Dictionary<Guid, EditorWorkspaceTabs> _editorWorkspaces = new();
    private readonly Dictionary<Guid, BrowserWorkspaceTabs> _browserWorkspaces = new();
    private readonly TerminalWorkspaceTabs _scratchTerminalWorkspace = new();
    private readonly EditorWorkspaceTabs _scratchEditorWorkspace = new();
    private readonly BrowserWorkspaceTabs _scratchBrowserWorkspace = new();
    private List<TerminalTab> _terminalTabs = new();
    private List<EditorTab> _editorTabs = new();
    private List<BrowserTab> _browserTabs = new();
    private TerminalWorkspaceTabs? _activeTerminalWorkspace;
    private EditorWorkspaceTabs? _activeEditorWorkspace;
    private BrowserWorkspaceTabs? _activeBrowserWorkspace;
    private TerminalTab? _activeTerminalTab;
    private EditorTab? _activeEditorTab;
    private BrowserTab? _activeBrowserTab;
    private WorkspaceSnapshot? _activeWorkspace;
    private bool _isSwitchingWorkspace;
    private const string DefaultBrowserUrl = "https://www.google.com/";

    /// <summary>サイドバーを閉じる直前の幅を保持し、再表示時に復元する。</summary>
    private GridLength _savedSidebarWidth = new(220);

    /// <summary>メイン領域のレイアウトツリー（リーフ＝ペイン、スプリット＝行/列の入れ子）。</summary>
    private PaneNode? _root;
    /// <summary>ペイン種別 → そのライブコントロールを内包するルート要素。</summary>
    private readonly Dictionary<PaneKind, FrameworkElement> _paneElements = new();
    /// <summary>ドラッグ判定中に一時的にマウスを捕捉しているタイトル要素。</summary>
    private FrameworkElement? _dragHandle;
    private Point _paneDragStart;
    private bool _paneDragArmed;

    // ===== ドラッグ中のスナップ風プレビュー（WebView2 の airspace を越えるため最前面 Popup を使う） =====
    private Popup? _dragPopup;
    private Canvas? _dragCanvas;
    private Border? _dragPreview;       // ドロップ先の半分を塗るプレビュー矩形
    private Border? _dragTargetOutline; // ドロップ先ペイン全体の枠
    private bool _paneDragging;
    private PaneKind _dragSource;
    private PaneKind? _dragTarget;
    private DropZone? _dragZone;

    // ===== ペイン間フォーカス移動（Ctrl+W h/j/k/l） =====
    /// <summary>直近でキーボードフォーカスを得たペイン（移動の起点）。</summary>
    private PaneKind? _focusedPane;
    /// <summary>Ctrl+W プレフィックスを受け取り、次の h/j/k/l を待ち受けている状態。</summary>
    private bool _awaitingPaneDirection;

    public ShellWindow(
        ShellViewModel vm,
        TerminalService terminal,
        EditorService editor,
        IWorkspaceService workspace,
        TabIconService tabIcons)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        _terminal = terminal;
        _editor = editor;
        _workspace = workspace;
        _tabIcons = tabIcons;
        _terminalTabs = _scratchTerminalWorkspace.Tabs;
        _editorTabs = _scratchEditorWorkspace.Tabs;
        _browserTabs = _scratchBrowserWorkspace.Tabs;

        InitializePanes();

        // サイドバーの開閉に追従して列幅・スプリッターを切り替える
        vm.PropertyChanged += OnShellPropertyChanged;
        vm.Tabs.TabActivated += OnSidebarTabActivated;
        vm.Tabs.TabCloseRequested += OnSidebarTabCloseRequested;
        vm.Workspaces.WorkspaceActivated += OnWorkspaceActivated;
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        Loaded += OnLoaded;

        // Ctrl+W に続けて h/j/k/l でフォーカスを上下左右の隣接ペインへ移す（vim 風）。
        // Terminal/Editor/AI は WPF コントロールなのでトンネリングの PreviewKeyDown で本体より先に拾う。
        // Browser(WebView2) は別 HWND でキー入力を内部消費するため、Browser ペインに
        // フォーカスがある間はこのナビゲーションは効かない（既知の制限）。
        PreviewKeyDown += OnPaneNavKey;
        PreviewGotKeyboardFocus += OnWindowPreviewGotKeyboardFocus;
        Deactivated += OnWindowDeactivated;

        var startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // sk0ya コントロールを生成してホストへ配置し、サービスへ結びつける
        var termTab = CreateTerminalTab(startDir);
        _terminalTabs.Add(termTab);
        TerminalContentHost.Children.Add(termTab.View);
        _vm.Tabs.AddTerminalTab(termTab.Id, termTab.View.HeaderTitle, false);
        ActivateTerminalTab(termTab.Id);
        _terminal.SetWorkingDirectory(startDir);
        UpdateTerminalTab(termTab, termTab.View.HeaderTitle);

        var editorTab = CreateEditorTab();
        _editorTabs.Add(editorTab);
        EditorContentHost.Children.Add(editorTab.Control);
        _vm.Tabs.AddEditorTab(editorTab.Id, editorTab.Control.FilePath, editorTab.Control.IsModified, false);
        ActivateEditorTab(editorTab.Id);
        UpdateEditorTab(editorTab);

        // フォルダを開いたらエージェントの作業ディレクトリを同期
        _workspace.RootChanged += (_, root) =>
        {
            if (!_isSwitchingWorkspace && !string.IsNullOrEmpty(root))
                _terminal.SetWorkingDirectory(root);
            if (_activeTerminalTab is { } activeTerminal)
                UpdateTerminalTab(activeTerminal, activeTerminal.View.HeaderTitle);
        };

        // FolderTree の単クリックは選択だけ、ダブルクリックで新しいエディタタブを開く。
        vm.FolderTree.FileActivated += async (_, path) => await OpenFileInNewEditorTabAsync(path);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm.Workspaces.ActiveWorkspace is { } workspace)
                await SwitchWorkspaceAsync(workspace, captureCurrent: false);
            else
            {
                ApplyDefaultLayout();
                BrowserAddressBox.Text = DefaultBrowserUrl;
                await CreateBrowserTabAsync(DefaultBrowserUrl);
            }
        }
        catch (Exception ex)
        {
            BrowserAddressBox.Text = $"WebView2 initialization failed: {ex.Message}";
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsSidebarVisible) && sender is ShellViewModel vm)
            ApplySidebarVisibility(vm.IsSidebarVisible);
    }

    private void ApplySidebarVisibility(bool visible)
    {
        if (visible)
        {
            SidebarColumn.MinWidth = 120;
            SidebarColumn.Width = _savedSidebarWidth.Value > 0 ? _savedSidebarWidth : new GridLength(220);
            SidebarSplitterColumn.Width = new GridLength(4);
            SidebarContainer.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            _savedSidebarWidth = SidebarColumn.Width;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarContainer.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }
    }

    // ===== ペインレイアウト（2D並べ替え・リサイズ・表示切替） =====

    private enum DropZone { Left, Right, Above, Below }
    private enum SplitKind { Rows, Columns }

    private void InitializePanes()
    {
        _paneElements[PaneKind.Terminal] = TerminalPane;
        _paneElements[PaneKind.Editor] = EditorPane;
        _paneElements[PaneKind.Browser] = BrowserPane;
        _paneElements[PaneKind.Ai] = AiPane;
        // レイアウトの構築は OnLoaded（ワークスペース適用 or 既定）に一本化する。
    }

    /// <summary>既定レイアウト：[Editor | Browser] / [Terminal] / [AI]。</summary>
    private void ApplyDefaultLayout()
    {
        var top = new PaneSplit { Orientation = SplitKind.Columns, Weight = 2 };
        top.Children.Add(NewLeaf(PaneKind.Editor));
        top.Children.Add(NewLeaf(PaneKind.Browser));

        var root = new PaneSplit { Orientation = SplitKind.Rows };
        root.Children.Add(top);
        root.Children.Add(NewLeaf(PaneKind.Terminal));
        root.Children.Add(NewLeaf(PaneKind.Ai));

        _root = root;
        RebuildPaneLayout();
    }

    private PaneLeaf NewLeaf(PaneKind kind) => new() { Kind = kind };

    /// <summary>ツリー内のすべてのリーフ（ペイン）を列挙する。</summary>
    private IEnumerable<PaneLeaf> AllLeaves(PaneNode? node = null)
    {
        node ??= _root;
        if (node is PaneLeaf leaf)
        {
            yield return leaf;
        }
        else if (node is PaneSplit split)
        {
            foreach (var child in split.Children)
                foreach (var l in AllLeaves(child))
                    yield return l;
        }
    }

    private PaneLeaf? FindLeaf(PaneKind kind) => AllLeaves().FirstOrDefault(l => l.Kind == kind);

    /// <summary>指定ノードを直接の子に持つスプリットを返す（ルートなら null）。</summary>
    private PaneSplit? FindParent(PaneNode target, PaneNode? current = null)
    {
        current ??= _root;
        if (current is not PaneSplit split)
            return null;
        if (split.Children.Contains(target))
            return split;
        foreach (var child in split.Children)
        {
            var found = FindParent(target, child);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// 現在の <see cref="_root"/>（レイアウトツリー）に合わせて <c>PaneHost</c> を組み直す。
    /// スプリットごとに Grid を生成し、ペイン本体はその Grid へ再ペアレントする
    /// （同一ウィンドウ内のため WebView2 等は生存する）。
    /// </summary>
    private void RebuildPaneLayout()
    {
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

        var border = (Brush)FindResource("Border");
        var visual = BuildNode(_root, border);
        if (visual is null)
        {
            // 可視ペインが1枚も無い（理論上は起きない）場合は既定へ戻す。
            ApplyDefaultLayout();
            return;
        }
        PaneHost.Children.Add(visual);
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
                AddTrack(grid, cols, new GridLength(4));
                var splitter = NewSplitter(cols, border);
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
    private bool IsNodeVisible(PaneNode node) => node switch
    {
        PaneLeaf leaf => !leaf.Hidden,
        PaneSplit split => split.Children.Any(IsNodeVisible),
        _ => false
    };

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

    private static GridSplitter NewSplitter(bool cols, Brush border) => new()
    {
        Width = cols ? 4 : double.NaN,
        Height = cols ? double.NaN : 4,
        ResizeDirection = cols ? GridResizeDirection.Columns : GridResizeDirection.Rows,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Background = border
    };

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
                        var w = grid.ColumnDefinitions[index].Width;
                        if (w.IsStar && w.Value > 0)
                            child.Weight = w.Value;
                    }
                }
                else
                {
                    if (index < grid.RowDefinitions.Count)
                    {
                        var h = grid.RowDefinitions[index].Height;
                        if (h.IsStar && h.Value > 0)
                            child.Weight = h.Value;
                    }
                }
            }
        }

        foreach (var child in split.Children)
            CaptureNode(child);
    }

    /// <summary>保存済みレイアウトを適用する。非表示ペインはリーフの Hidden で復元する。</summary>
    private void ApplyPaneLayout(PaneNodeSnapshot? snapshot)
    {
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
    {
        if (snap.Children is { Count: > 0 })
        {
            var kids = new List<PaneNode>();
            foreach (var child in snap.Children)
            {
                var n = BuildFromSnapshot(child, seen);
                if (n is not null)
                    kids.Add(n);
            }
            if (kids.Count == 0)
                return null;
            if (kids.Count == 1)
            {
                kids[0].Weight = snap.Weight > 0 ? snap.Weight : 1;
                return kids[0];
            }
            var split = new PaneSplit
            {
                Orientation = snap.Orientation == "Columns" ? SplitKind.Columns : SplitKind.Rows,
                Weight = snap.Weight > 0 ? snap.Weight : 1
            };
            split.Children.AddRange(kids);
            return split;
        }

        if (snap.Kind is { } kind && _paneElements.ContainsKey(kind) && seen.Add(kind))
            return new PaneLeaf { Kind = kind, Weight = snap.Weight > 0 ? snap.Weight : 1, Hidden = snap.Hidden };
        return null;
    }

    private static PaneNodeSnapshot ToSnapshot(PaneNode node)
    {
        if (node is PaneLeaf leaf)
            return new PaneNodeSnapshot { Weight = leaf.Weight, Kind = leaf.Kind, Hidden = leaf.Hidden };

        var split = (PaneSplit)node;
        return new PaneNodeSnapshot
        {
            Weight = split.Weight,
            Orientation = split.Orientation == SplitKind.Columns ? "Columns" : "Rows",
            Children = split.Children.Select(ToSnapshot).ToList()
        };
    }

    /// <summary>
    /// ツリーを正規化する：空スプリットを除去し、子が1つのスプリットを畳み、
    /// 同方向に入れ子になったスプリットをフラット化する。
    /// </summary>
    private PaneNode? Normalize(PaneNode? node)
    {
        if (node is not PaneSplit split)
            return node;

        var kids = new List<PaneNode>();
        foreach (var child in split.Children)
        {
            var n = Normalize(child);
            if (n is null)
                continue;
            if (n is PaneSplit inner && inner.Orientation == split.Orientation)
                kids.AddRange(inner.Children); // 同方向はフラット化
            else
                kids.Add(n);
        }

        if (kids.Count == 0)
            return null;
        if (kids.Count == 1)
        {
            kids[0].Weight = split.Weight;
            return kids[0];
        }

        split.Children.Clear();
        split.Children.AddRange(kids);
        return split;
    }

    private void OnPaneTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        _paneDragStart = e.GetPosition(null);
        _paneDragArmed = true;
        // しきい値到達前にカーソルがタイトルを外れても MouseMove を拾えるよう捕捉する。
        if (sender is FrameworkElement fe && fe.CaptureMouse())
            _dragHandle = fe;
    }

    private void OnPaneTitleMouseMove(object sender, MouseEventArgs e)
    {
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
            DisarmTitleDrag(); // タイトルの捕捉を解放してからオーバーレイへ捕捉を移す
            BeginPaneDrag(kind);
        }
    }

    private void OnPaneTitleMouseUp(object sender, MouseButtonEventArgs e) => DisarmTitleDrag();

    /// <summary>ドラッグ判定を解除し、タイトル要素のマウス捕捉を解放する。</summary>
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

    /// <summary>
    /// スナップ風のレイアウト・ドラッグを開始する。ドラッグ中は最前面の透明 Popup を被せ、
    /// その上でマウスを追跡＆プレビューを描画する（WebView2/Terminal の上でも正しく重なる）。
    /// </summary>
    private void BeginPaneDrag(PaneKind source)
    {
        if (VisibleLeafCount() <= 1)
            return; // 1枚だけなら移動先がない

        EnsureDragOverlay();
        _dragSource = source;
        _dragTarget = null;
        _dragZone = null;
        _paneDragging = true;

        _dragCanvas!.Width = PaneHost.ActualWidth;
        _dragCanvas.Height = PaneHost.ActualHeight;
        _dragPreview!.Visibility = Visibility.Collapsed;
        _dragTargetOutline!.Visibility = Visibility.Collapsed;
        _dragPopup!.IsOpen = true;

        // 開いた直後は Popup の HWND が未実体化で捕捉に失敗することがあるため、失敗時は次の入力で再試行する。
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
        if (_dragPopup is not null)
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
        _dragCanvas = new Canvas { Background = Brushes.Transparent };
        _dragCanvas.Children.Add(_dragTargetOutline);
        _dragCanvas.Children.Add(_dragPreview);
        _dragCanvas.MouseMove += OnDragCanvasMouseMove;
        _dragCanvas.MouseLeftButtonUp += OnDragCanvasMouseUp;
        _dragCanvas.LostMouseCapture += OnDragCanvasLostCapture;

        _dragPopup = new Popup
        {
            PlacementTarget = PaneHost,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
            Child = _dragCanvas
        };
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

        UpdateDragPreview(e.GetPosition(PaneHost));
    }

    private void UpdateDragPreview(Point pos)
    {
        var hit = HitTestCell(pos);
        if (hit is null)
        {
            _dragTarget = null;
            _dragZone = null;
            _dragPreview!.Visibility = Visibility.Collapsed;
            _dragTargetOutline!.Visibility = Visibility.Collapsed;
            return;
        }

        var (kind, rect) = hit.Value;
        var relX = rect.Width > 0 ? (pos.X - rect.X) / rect.Width : 0.5;
        var relY = rect.Height > 0 ? (pos.Y - rect.Y) / rect.Height : 0.5;
        var zone = NearestZone(relX, relY);
        _dragTarget = kind;
        _dragZone = zone;

        PlaceOverlay(_dragTargetOutline!, rect);
        PlaceOverlay(_dragPreview!, ZoneRect(rect, zone));
        _dragTargetOutline!.Visibility = Visibility.Visible;
        _dragPreview!.Visibility = Visibility.Visible;
    }

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
        EndPaneDrag();

        if (target is { } t && zone is { } z && t != source)
            MovePane(source, t, z);
    }

    private void OnDragCanvasLostCapture(object sender, MouseEventArgs e)
    {
        if (_paneDragging)
            EndPaneDrag();
    }

    private void EndPaneDrag()
    {
        _paneDragging = false;
        if (ReferenceEquals(Mouse.Captured, _dragCanvas))
            Mouse.Capture(null);
        if (_dragPopup is not null)
            _dragPopup.IsOpen = false;
        if (_dragPreview is not null)
            _dragPreview.Visibility = Visibility.Collapsed;
        if (_dragTargetOutline is not null)
            _dragTargetOutline.Visibility = Visibility.Collapsed;
    }

    /// <summary>カーソル位置にあるペインのセルとその矩形（PaneHost 座標）を返す。</summary>
    private (PaneKind Kind, Rect Rect)? HitTestCell(Point pos)
    {
        foreach (var leaf in AllLeaves())
        {
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

    private void MovePane(PaneKind source, PaneKind target, DropZone zone)
    {
        if (source == target)
            return;

        var sourceLeaf = FindLeaf(source);
        var targetLeaf = FindLeaf(target);
        if (sourceLeaf is null || targetLeaf is null)
            return;

        CaptureLayoutSizes();

        // 移動元をツリーから外し、ターゲットの指定した辺へ挿入する。
        RemoveNode(sourceLeaf);
        sourceLeaf.Weight = 1;
        InsertRelative(sourceLeaf, targetLeaf, zone);

        _root = Normalize(_root);
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>ノードを親スプリットから取り外す（畳み込みは Normalize に任せる）。</summary>
    private void RemoveNode(PaneNode node)
    {
        var parent = FindParent(node);
        if (parent is null)
            _root = null;
        else
            parent.Children.Remove(node);
    }

    /// <summary>
    /// <paramref name="node"/> を <paramref name="target"/> の指定した辺へ挿入する。
    /// 望む方向が target の親スプリットと一致すれば兄弟として差し込み、
    /// 異なれば target を新しいスプリットで包む（＝列の片方だけを上下分割できる）。
    /// </summary>
    private void InsertRelative(PaneNode node, PaneLeaf target, DropZone zone)
    {
        var wantColumns = zone is DropZone.Left or DropZone.Right;
        var before = zone is DropZone.Left or DropZone.Above;
        var desired = wantColumns ? SplitKind.Columns : SplitKind.Rows;
        var parent = FindParent(target);

        if (parent is not null && parent.Orientation == desired)
        {
            var index = parent.Children.IndexOf(target);
            parent.Children.Insert(before ? index : index + 1, node);
            return;
        }

        // target を内包する新しいスプリットへ置き換える。
        var split = new PaneSplit { Orientation = desired, Weight = target.Weight };
        target.Weight = 1;
        if (before)
        {
            split.Children.Add(node);
            split.Children.Add(target);
        }
        else
        {
            split.Children.Add(target);
            split.Children.Add(node);
        }

        if (parent is null)
            _root = split;
        else
            parent.Children[parent.Children.IndexOf(target)] = split;
    }

    private void OnHidePane(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaneKind>(tag, out var kind))
            SetPaneVisible(kind, false);
    }

    private void OnTogglePaneVisibility(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<PaneKind>(tag, out var kind))
            SetPaneVisible(kind, !IsPaneVisible(kind));
    }

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
        if (currentlyVisible == visible)
            return;

        CaptureLayoutSizes();

        if (visible)
        {
            if (leaf is null)
                AddLeafAtBottom(NewLeaf(kind)); // 一度もツリーに置かれていないペイン
            else
                leaf.Hidden = false;
        }
        else
        {
            // 最後の1枚は隠さない
            if (VisibleLeafCount() <= 1)
                return;
            leaf!.Hidden = true;
            if (_focusedPane == kind)
                _focusedPane = null; // 起点が消えたので次回ナビゲーションは可視ペインから選び直す
        }

        _root = Normalize(_root);
        RebuildPaneLayout();
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>再表示するペインを最下段の新しい行として追加する。</summary>
    private void AddLeafAtBottom(PaneLeaf leaf)
    {
        if (_root is null)
        {
            _root = leaf;
        }
        else if (_root is PaneSplit split && split.Orientation == SplitKind.Rows)
        {
            split.Children.Add(leaf);
        }
        else
        {
            var rows = new PaneSplit { Orientation = SplitKind.Rows };
            rows.Children.Add(_root);
            rows.Children.Add(leaf);
            _root = rows;
        }
    }

    // ===== ペイン間フォーカス移動（Ctrl+W → h/j/k/l） =====

    /// <summary>
    /// Ctrl+W を押すと方向キー（h/j/k/l）待ちに入り、続けて押されたキーの向きの
    /// 隣接ペインへフォーカスを移す。Preview（トンネル）で本体より先に拾い、消費したキーは
    /// <see cref="RoutedEventArgs.Handled"/> で止める（同一イベントの KeyDown も併せて抑止される）。
    /// </summary>
    private void OnPaneNavKey(object sender, KeyEventArgs e)
    {
        var key = e.Key;

        if (_awaitingPaneDirection)
        {
            if (IsModifierKey(key))
                return; // Ctrl 等の単独押下は方向キー待ちを維持する

            // Ctrl+W の押しっぱなし・再入力はプレフィックスのまま待ち続ける
            if (key == Key.W && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                return;
            }

            _awaitingPaneDirection = false;
            if (MapNavDirection(key) is { } direction)
            {
                FocusPaneInDirection(direction);
                e.Handled = true;
            }
            // 方向キー以外はプレフィックスを解除し、そのまま素通しさせる
            return;
        }

        if (key == Key.W && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _awaitingPaneDirection = true;
            e.Handled = true;
        }
    }

    private static DropZone? MapNavDirection(Key key) => key switch
    {
        Key.H => DropZone.Left,
        Key.J => DropZone.Below,
        Key.K => DropZone.Above,
        Key.L => DropZone.Right,
        _ => null
    };

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.System or Key.LWin or Key.RWin;

    /// <summary>キーボードフォーカスが入ったペインを記録する（移動の起点に使う）。</summary>
    private void OnWindowPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // フォーカスが他所へ移ったら、待ち状態の Ctrl+W プレフィックスは破棄する。
        // （Ctrl+W → 気が変わってクリック/別ペインへ移動 → 後続の h/j/k/l が誤って奪われるのを防ぐ）
        _awaitingPaneDirection = false;

        if (e.NewFocus is DependencyObject d && FindPaneOf(d) is { } kind)
            _focusedPane = kind;
    }

    /// <summary>ウィンドウが非アクティブになったら Ctrl+W の待ち状態を解除する。</summary>
    private void OnWindowDeactivated(object? sender, EventArgs e) => _awaitingPaneDirection = false;

    /// <summary>要素を内包するペイン種別を視覚ツリーを遡って特定する（ペイン外なら null）。</summary>
    private PaneKind? FindPaneOf(DependencyObject element)
    {
        for (var current = element; current is not null; current = GetAnyParent(current))
        {
            foreach (var (kind, paneElement) in _paneElements)
                if (ReferenceEquals(paneElement, current))
                    return kind;
        }
        return null;
    }

    private static DependencyObject? GetAnyParent(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    /// <summary>起点ペインから指定方向で最も近い隣接ペインへフォーカスを移す。</summary>
    private void FocusPaneInDirection(DropZone direction)
    {
        var origin = _focusedPane ?? AllLeaves().FirstOrDefault(l => !l.Hidden)?.Kind;
        if (origin is not { } originKind || !TryGetPaneRect(originKind, out var from))
            return;

        var fromCenter = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
        PaneKind? best = null;
        var bestScore = double.MaxValue;

        foreach (var leaf in AllLeaves())
        {
            if (leaf.Hidden || leaf.Kind == originKind || !TryGetPaneRect(leaf.Kind, out var r))
                continue;

            // 指定方向の側にあるペインだけを候補にする（タイル配置なので辺で判定）。
            const double tolerance = 1.0;
            var inDirection = direction switch
            {
                DropZone.Left => r.X + r.Width <= from.X + tolerance,
                DropZone.Right => r.X >= from.X + from.Width - tolerance,
                DropZone.Above => r.Y + r.Height <= from.Y + tolerance,
                _ => r.Y >= from.Y + from.Height - tolerance,
            };
            if (!inDirection)
                continue;

            var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            // 移動軸方向の距離を主に、直交方向のずれを従にして最も近いペインを選ぶ。
            var (axis, perpendicular) = direction is DropZone.Left or DropZone.Right
                ? (Math.Abs(center.X - fromCenter.X), Math.Abs(center.Y - fromCenter.Y))
                : (Math.Abs(center.Y - fromCenter.Y), Math.Abs(center.X - fromCenter.X));
            var score = axis + perpendicular * 2;
            if (score < bestScore)
            {
                bestScore = score;
                best = leaf.Kind;
            }
        }

        if (best is { } target)
            FocusPane(target);
    }

    /// <summary>ペイン本体の矩形（PaneHost 座標系）を取得する。非表示・未配置なら false。</summary>
    private bool TryGetPaneRect(PaneKind kind, out Rect rect)
    {
        rect = default;
        if (!_paneElements.TryGetValue(kind, out var element)
            || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        var topLeft = element.TransformToVisual(PaneHost).Transform(new Point(0, 0));
        rect = new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
        return true;
    }

    /// <summary>指定ペインのアクティブな中身へキーボードフォーカスを移す。</summary>
    private void FocusPane(PaneKind kind)
    {
        _focusedPane = kind;
        switch (kind)
        {
            case PaneKind.Terminal:
                _activeTerminalTab?.View.FocusTerminal();
                break;
            case PaneKind.Editor:
                _activeEditorTab?.Control.Focus();
                break;
            case PaneKind.Browser:
                _activeBrowserTab?.View.Focus();
                break;
            case PaneKind.Ai:
                AiBarHost.FocusInput();
                break;
        }
    }

    private TerminalTab CreateTerminalTab(string startDirectory, Guid? requestedId = null)
    {
        var view = new TerminalTabView("pwsh.exe", startDirectory);
        var tab = new TerminalTab(requestedId ?? Guid.NewGuid(), view);
        view.HeaderTitleChanged += (_, title) => UpdateTerminalTab(tab, title);
        return tab;
    }

    private EditorTab CreateEditorTab(Guid? requestedId = null)
    {
        var control = new VimEditorControl
        {
            Visibility = Visibility.Collapsed
        };
        var tab = new EditorTab(requestedId ?? Guid.NewGuid(), control);
        control.BufferChanged += (_, _) => UpdateEditorTab(tab);
        control.SaveRequested += (_, _) => QueueEditorTabUpdate(tab);
        return tab;
    }

    private void QueueEditorTabUpdate(EditorTab tab)
    {
        _ = tab.Control.Dispatcher.BeginInvoke(new Action(() => UpdateEditorTab(tab)));
    }

    private void OnTabStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.ScrollableWidth <= 0)
            return;

        var nextOffset = Math.Clamp(
            scrollViewer.HorizontalOffset - e.Delta,
            0,
            scrollViewer.ScrollableWidth);

        scrollViewer.ScrollToHorizontalOffset(nextOffset);
        e.Handled = true;
    }

    // ===== カスタムタイトルバー（WindowChrome） =====

    // 単一の四角（最大化）/ 二重の四角（元に戻す）をベクターで描く。
    private static readonly Geometry MaximizeGeometry = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
    private static readonly Geometry RestoreGeometry = Geometry.Parse("M2.5,2.5 V0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z");

    private void OnMinimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnTerminalNewTab(object sender, RoutedEventArgs e)
    {
        var startDir = _activeTerminalTab?.View.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(startDir) || !Directory.Exists(startDir))
            startDir = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;

        var tab = CreateTerminalTab(startDir);
        _terminalTabs.Add(tab);
        TerminalContentHost.Children.Add(tab.View);
        _vm.Tabs.AddTerminalTab(tab.Id, $"Terminal {CurrentTerminalWorkspace.NextTabNumber++}", false);
        ActivateTerminalTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }

    private void OnTerminalTabSelected(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateTerminalTab(id);
    }

    private async void OnTerminalTabClosed(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            await CloseTerminalTabAsync(id);
            SaveActiveWorkspaceSnapshot();
        }
    }

    private void OnEditorNewTab(object sender, RoutedEventArgs e)
    {
        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        EditorContentHost.Children.Add(tab.Control);
        _vm.Tabs.AddEditorTab(tab.Id, null, false, false);
        ActivateEditorTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }

    private async Task OpenFileInNewEditorTabAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        EditorContentHost.Children.Add(tab.Control);
        _vm.Tabs.AddEditorTab(tab.Id, path, false, false);
        ActivateEditorTab(tab.Id);
        await _editor.OpenFileAsync(path);
        UpdateEditorTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    private void OnEditorTabSelected(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateEditorTab(id);
    }

    private void OnEditorTabClosed(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            CloseEditorTab(id);
            SaveActiveWorkspaceSnapshot();
        }
    }

    private void OnBrowserBack(object sender, RoutedEventArgs e)
    {
        var view = ActiveBrowserView;
        if (view?.CanGoBack == true)
            view.GoBack();
    }

    private void OnBrowserForward(object sender, RoutedEventArgs e)
    {
        var view = ActiveBrowserView;
        if (view?.CanGoForward == true)
            view.GoForward();
    }

    private void OnBrowserReload(object sender, RoutedEventArgs e)
        => ActiveBrowserView?.CoreWebView2?.Reload();

    private async void OnBrowserNewTab(object sender, RoutedEventArgs e)
    {
        await CreateBrowserTabAsync(DefaultBrowserUrl);
        SaveActiveWorkspaceSnapshot();
    }

    private void OnBrowserTabSelected(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
            ActivateBrowserTab(id);
    }

    private async void OnBrowserTabClosed(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id })
        {
            await CloseBrowserTabAsync(id);
            SaveActiveWorkspaceSnapshot();
        }
    }

    private void OnBrowserGo(object sender, RoutedEventArgs e)
        => NavigateBrowser(BrowserAddressBox.Text);

    private void OnBrowserAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateBrowser(BrowserAddressBox.Text);
            e.Handled = true;
        }
    }

    private void OnBrowserNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (sender is not WebView2 view)
            return;

        var tab = _browserTabs.FirstOrDefault(t => ReferenceEquals(t.View, view));
        if (tab is null)
            return;

        UpdateBrowserTab(tab);
        _ = RefreshBrowserTabIconAsync(tab);
        if (ReferenceEquals(_activeBrowserTab, tab) && view.Source is not null)
            BrowserAddressBox.Text = view.Source.ToString();
    }

    private void NavigateBrowser(string text)
    {
        var address = NormalizeBrowserAddress(text);
        BrowserAddressBox.Text = address;

        var view = ActiveBrowserView;
        if (view?.CoreWebView2 is not null)
        {
            view.CoreWebView2.Navigate(address);
            UpdateBrowserTab(_activeBrowserTab);
            SaveActiveWorkspaceSnapshot();
        }
    }

    private TerminalWorkspaceTabs CurrentTerminalWorkspace
        => _activeTerminalWorkspace ?? _scratchTerminalWorkspace;

    private EditorWorkspaceTabs CurrentEditorWorkspace
        => _activeEditorWorkspace ?? _scratchEditorWorkspace;

    private void ActivateTerminalTab(Guid id)
    {
        var tab = _terminalTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;

        foreach (var terminalTab in _terminalTabs)
            terminalTab.View.Visibility = terminalTab.Id == id ? Visibility.Visible : Visibility.Collapsed;

        _activeTerminalTab = tab;
        CurrentTerminalWorkspace.ActiveTabId = id;
        _terminal.Attach(tab.View);
        if (Directory.Exists(tab.View.WorkingDirectory))
            _terminal.SetWorkingDirectory(tab.View.WorkingDirectory);
        _vm.Tabs.ActivateTerminalTab(id);
        tab.View.FocusTerminal();
        SaveActiveWorkspaceSnapshot();
    }

    private async Task CloseTerminalTabAsync(Guid id)
    {
        var index = _terminalTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;

        var wasActive = _activeTerminalTab?.Id == id;
        var tab = _terminalTabs[index];
        TerminalContentHost.Children.Remove(tab.View);
        await tab.View.CloseAsync();
        _terminalTabs.RemoveAt(index);
        _vm.Tabs.RemoveTerminalTab(id);

        if (!wasActive)
            return;

        if (_terminalTabs.Count == 0)
        {
            var startDir = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
            var newTab = CreateTerminalTab(startDir);
            _terminalTabs.Add(newTab);
            TerminalContentHost.Children.Add(newTab.View);
            _vm.Tabs.AddTerminalTab(newTab.Id, "Terminal", false);
            ActivateTerminalTab(newTab.Id);
            return;
        }

        ActivateTerminalTab(_terminalTabs[Math.Min(index, _terminalTabs.Count - 1)].Id);
    }

    private void ActivateEditorTab(Guid id)
    {
        var tab = _editorTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;

        foreach (var editorTab in _editorTabs)
            editorTab.Control.Visibility = editorTab.Id == id ? Visibility.Visible : Visibility.Collapsed;

        _activeEditorTab = tab;
        CurrentEditorWorkspace.ActiveTabId = id;
        _editor.Attach(tab.Control);
        _vm.Tabs.ActivateEditorTab(id);
        tab.Control.Focus();
        SaveActiveWorkspaceSnapshot();
    }

    private void CloseEditorTab(Guid id)
    {
        var index = _editorTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;

        var wasActive = _activeEditorTab?.Id == id;
        var tab = _editorTabs[index];
        EditorContentHost.Children.Remove(tab.Control);
        _editorTabs.RemoveAt(index);
        _vm.Tabs.RemoveEditorTab(id);

        if (!wasActive)
            return;

        if (_editorTabs.Count == 0)
        {
            var newTab = CreateEditorTab();
            _editorTabs.Add(newTab);
            EditorContentHost.Children.Add(newTab.Control);
            _vm.Tabs.AddEditorTab(newTab.Id, null, false, false);
            ActivateEditorTab(newTab.Id);
            return;
        }

        ActivateEditorTab(_editorTabs[Math.Min(index, _editorTabs.Count - 1)].Id);
    }

    private WebView2? ActiveBrowserView => _activeBrowserTab?.View;

    private BrowserWorkspaceTabs CurrentBrowserWorkspace
        => _activeBrowserWorkspace ?? _scratchBrowserWorkspace;

    private async Task CreateBrowserTabAsync(string url, Guid? requestedId = null, string? requestedTitle = null)
    {
        var id = requestedId ?? Guid.NewGuid();
        var browserWorkspace = CurrentBrowserWorkspace;
        var normalizedUrl = NormalizeBrowserAddress(url);
        var view = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
            Visibility = Visibility.Collapsed
        };
        view.NavigationCompleted += OnBrowserNavigationCompleted;

        var tab = new BrowserTab(id, view);
        _browserTabs.Add(tab);
        BrowserContentHost.Children.Add(view);
        _vm.Tabs.AddBrowserTab(id, requestedTitle ?? $"Tab {browserWorkspace.NextTabNumber++}", false);
        ActivateBrowserTab(id);

        await view.EnsureCoreWebView2Async();
        view.CoreWebView2!.FaviconChanged += OnBrowserFaviconChanged;
        view.Source = new Uri(normalizedUrl);
        UpdateBrowserTab(tab);
        await RefreshBrowserTabIconAsync(tab);
    }

    private async Task CloseBrowserTabAsync(Guid id)
    {
        var index = _browserTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;

        var wasActive = _activeBrowserTab?.Id == id;
        var tab = _browserTabs[index];
        if (tab.View.CoreWebView2 is not null)
            tab.View.CoreWebView2.FaviconChanged -= OnBrowserFaviconChanged;
        BrowserContentHost.Children.Remove(tab.View);
        tab.View.NavigationCompleted -= OnBrowserNavigationCompleted;
        tab.View.Dispose();
        _browserTabs.RemoveAt(index);
        _vm.Tabs.RemoveBrowserTab(id);

        if (!wasActive)
            return;

        if (_browserTabs.Count == 0)
        {
            await CreateBrowserTabAsync(DefaultBrowserUrl);
            return;
        }

        ActivateBrowserTab(_browserTabs[Math.Min(index, _browserTabs.Count - 1)].Id);
    }

    private void ActivateBrowserTab(Guid id)
    {
        var tab = _browserTabs.FirstOrDefault(t => t.Id == id);
        if (tab is null)
            return;

        foreach (var browserTab in _browserTabs)
            browserTab.View.Visibility = browserTab.Id == id ? Visibility.Visible : Visibility.Collapsed;

        _activeBrowserTab = tab;
        CurrentBrowserWorkspace.ActiveTabId = id;
        _vm.Tabs.ActivateBrowserTab(id);
        BrowserAddressBox.Text = tab.View.Source?.ToString() ?? string.Empty;
        tab.View.Focus();
        SaveActiveWorkspaceSnapshot();
    }

    private void UpdateBrowserTab(BrowserTab? tab)
    {
        if (tab is null)
            return;

        _vm.Tabs.UpdateBrowserTab(tab.Id, tab.View.CoreWebView2?.DocumentTitle);
        SaveActiveWorkspaceSnapshot();
    }

    private async void OnBrowserFaviconChanged(object? sender, object? e)
    {
        if (sender is not Microsoft.Web.WebView2.Core.CoreWebView2 coreWebView2)
            return;

        var tab = _browserTabs.FirstOrDefault(t => ReferenceEquals(t.View.CoreWebView2, coreWebView2));
        if (tab is null)
            return;

        await RefreshBrowserTabIconAsync(tab);
    }

    private async Task RefreshBrowserTabIconAsync(BrowserTab tab)
    {
        if (tab.View.CoreWebView2 is null)
            return;

        var icon = await _tabIcons.GetBrowserIconAsync(tab.View.CoreWebView2, tab.View.Source?.ToString());
        _vm.Tabs.UpdateTabIcon(tab.Id, icon);
    }

    /// <summary>レイアウトツリーのノード基底。</summary>
    private abstract class PaneNode
    {
        /// <summary>親スプリット内での star 比率。</summary>
        public double Weight { get; set; } = 1;
        /// <summary>直近の描画で割り当てられた Grid トラック番号（未描画は -1）。サイズ取り込み用。</summary>
        public int TrackIndex { get; set; } = -1;
    }

    /// <summary>リーフ＝1ペイン。</summary>
    private sealed class PaneLeaf : PaneNode
    {
        public PaneKind Kind { get; init; }
        /// <summary>非表示中か。true でもツリーには残し、再表示で元の位置・比率へ戻す。</summary>
        public bool Hidden { get; set; }
    }

    /// <summary>スプリット＝入れ子の行（上下）または列（左右）。</summary>
    private sealed class PaneSplit : PaneNode
    {
        public SplitKind Orientation { get; init; }
        public List<PaneNode> Children { get; } = new();
        /// <summary>再構築のたびに生成される Grid。サイズ取り込み用の一時参照。</summary>
        public Grid? Host { get; set; }
    }

    private sealed record TerminalTab(Guid Id, TerminalTabView View);
    private sealed record EditorTab(Guid Id, VimEditorControl Control);
    private sealed record BrowserTab(Guid Id, WebView2 View);

    private sealed class TerminalWorkspaceTabs
    {
        public List<TerminalTab> Tabs { get; } = new();
        public Guid? ActiveTabId { get; set; }
        public int NextTabNumber { get; set; } = 1;
        public bool IsInitialized { get; set; }
    }

    private sealed class EditorWorkspaceTabs
    {
        public List<EditorTab> Tabs { get; } = new();
        public Guid? ActiveTabId { get; set; }
        public bool IsInitialized { get; set; }
    }

    private sealed class BrowserWorkspaceTabs
    {
        public List<BrowserTab> Tabs { get; } = new();
        public Guid? ActiveTabId { get; set; }
        public int NextTabNumber { get; set; } = 1;
        public bool IsInitialized { get; set; }
    }

    private void OnSidebarTabActivated(object? sender, TabEntryViewModel tab)
    {
        switch (tab.Kind)
        {
            case TabEntryKind.Terminal:
                ActivateTerminalTab(tab.Id);
                break;
            case TabEntryKind.Editor:
                ActivateEditorTab(tab.Id);
                break;
            case TabEntryKind.Browser:
                ActivateBrowserTab(tab.Id);
                break;
        }
    }

    private async void OnSidebarTabCloseRequested(object? sender, TabEntryViewModel tab)
    {
        switch (tab.Kind)
        {
            case TabEntryKind.Terminal:
                await CloseTerminalTabAsync(tab.Id);
                break;
            case TabEntryKind.Editor:
                CloseEditorTab(tab.Id);
                break;
            case TabEntryKind.Browser:
                await CloseBrowserTabAsync(tab.Id);
                break;
        }
    }

    private void UpdateTerminalTab(TerminalTab tab, string? title)
    {
        _vm.Tabs.UpdateTerminalTab(tab.Id, title);
        SaveActiveWorkspaceSnapshot();
    }

    private void UpdateEditorTab(EditorTab tab)
    {
        _vm.Tabs.UpdateEditorTab(tab.Id, tab.Control.FilePath, tab.Control.IsModified);
        SaveActiveWorkspaceSnapshot();
    }

    private async void OnWorkspaceActivated(object? sender, WorkspaceSnapshot workspace)
        => await SwitchWorkspaceAsync(workspace, captureCurrent: true);

    private async Task SwitchWorkspaceAsync(WorkspaceSnapshot workspace, bool captureCurrent)
    {
        if (captureCurrent)
            SaveActiveWorkspaceSnapshot();

        _isSwitchingWorkspace = true;
        try
        {
            DetachTerminalTabs();
            DetachEditorTabs();
            DetachBrowserTabs();
            _activeWorkspace = workspace;
            _vm.FolderTree.LoadRoot(workspace.RootPath);
            ApplyPaneLayout(workspace.PaneLayout);

            RestoreTerminalTabs(workspace);
            RestoreEditorTabs(workspace);
            await RestoreBrowserTabsAsync(workspace);
        }
        finally
        {
            _isSwitchingWorkspace = false;
        }

        SaveActiveWorkspaceSnapshot();
    }

    private static void RestoreEditor(VimEditorControl editor, EditorTabSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.FilePath) && File.Exists(snapshot.FilePath))
        {
            editor.LoadFile(snapshot.FilePath);
            if (!snapshot.IsModified)
                return;
        }

        if (snapshot.IsModified || string.IsNullOrWhiteSpace(snapshot.FilePath))
        {
            editor.SetText(snapshot.Text ?? string.Empty);
            return;
        }

        editor.SetText(string.Empty);
    }

    private void RestoreTerminalTabs(WorkspaceSnapshot workspace)
    {
        var terminalWorkspace = GetOrCreateTerminalWorkspace(workspace.Id);
        _activeTerminalWorkspace = terminalWorkspace;
        _terminalTabs = terminalWorkspace.Tabs;

        if (terminalWorkspace.IsInitialized && _terminalTabs.Count > 0)
        {
            AttachTerminalTabs();
            ActivateTerminalTab(terminalWorkspace.ActiveTabId ?? _terminalTabs[0].Id);
            return;
        }

        terminalWorkspace.IsInitialized = true;
        var snapshots = workspace.TerminalTabs.Count == 0
            ? new[]
            {
                new TerminalTabSnapshot
                {
                    WorkingDirectory = workspace.Terminal.WorkingDirectory,
                    Title = workspace.Terminal.Title ?? "Terminal",
                    IsActive = true
                }
            }
            : workspace.TerminalTabs.ToArray();

        foreach (var snapshot in snapshots)
        {
            var cwd = Directory.Exists(snapshot.WorkingDirectory) ? snapshot.WorkingDirectory! : workspace.RootPath;
            var tab = CreateTerminalTab(cwd, snapshot.Id == Guid.Empty ? null : snapshot.Id);
            _terminalTabs.Add(tab);
            TerminalContentHost.Children.Add(tab.View);
            _vm.Tabs.AddTerminalTab(tab.Id, snapshot.Title ?? tab.View.HeaderTitle, false);
        }

        var active = snapshots.FirstOrDefault(t => t.IsActive) ?? snapshots.First();
        ActivateTerminalTab(active.Id == Guid.Empty ? _terminalTabs[0].Id : active.Id);
    }

    private void RestoreEditorTabs(WorkspaceSnapshot workspace)
    {
        var editorWorkspace = GetOrCreateEditorWorkspace(workspace.Id);
        _activeEditorWorkspace = editorWorkspace;
        _editorTabs = editorWorkspace.Tabs;

        if (editorWorkspace.IsInitialized && _editorTabs.Count > 0)
        {
            AttachEditorTabs();
            ActivateEditorTab(editorWorkspace.ActiveTabId ?? _editorTabs[0].Id);
            return;
        }

        editorWorkspace.IsInitialized = true;
        var snapshots = workspace.EditorTabs.Count == 0
            ? new[]
            {
                new EditorTabSnapshot
                {
                    FilePath = workspace.Editor.FilePath,
                    Text = workspace.Editor.Text,
                    IsModified = workspace.Editor.IsModified,
                    IsActive = true
                }
            }
            : workspace.EditorTabs.ToArray();

        foreach (var snapshot in snapshots)
        {
            var tab = CreateEditorTab(snapshot.Id == Guid.Empty ? null : snapshot.Id);
            RestoreEditor(tab.Control, snapshot);
            _editorTabs.Add(tab);
            EditorContentHost.Children.Add(tab.Control);
            _vm.Tabs.AddEditorTab(tab.Id, snapshot.FilePath, snapshot.IsModified, false);
        }

        var active = snapshots.FirstOrDefault(t => t.IsActive) ?? snapshots.First();
        ActivateEditorTab(active.Id == Guid.Empty ? _editorTabs[0].Id : active.Id);
    }

    private TerminalWorkspaceTabs GetOrCreateTerminalWorkspace(Guid workspaceId)
    {
        if (_terminalWorkspaces.TryGetValue(workspaceId, out var terminalWorkspace))
            return terminalWorkspace;

        terminalWorkspace = new TerminalWorkspaceTabs();
        _terminalWorkspaces[workspaceId] = terminalWorkspace;
        return terminalWorkspace;
    }

    private EditorWorkspaceTabs GetOrCreateEditorWorkspace(Guid workspaceId)
    {
        if (_editorWorkspaces.TryGetValue(workspaceId, out var editorWorkspace))
            return editorWorkspace;

        editorWorkspace = new EditorWorkspaceTabs();
        _editorWorkspaces[workspaceId] = editorWorkspace;
        return editorWorkspace;
    }

    private void DetachTerminalTabs()
    {
        CurrentTerminalWorkspace.ActiveTabId = _activeTerminalTab?.Id;
        TerminalContentHost.Children.Clear();
        _vm.Tabs.TerminalTabs.Clear();
        _activeTerminalTab = null;
    }

    private void AttachTerminalTabs()
    {
        TerminalContentHost.Children.Clear();
        _vm.Tabs.TerminalTabs.Clear();

        foreach (var tab in _terminalTabs)
        {
            if (!TerminalContentHost.Children.Contains(tab.View))
                TerminalContentHost.Children.Add(tab.View);

            _vm.Tabs.AddTerminalTab(tab.Id, tab.View.HeaderTitle, false);
        }
    }

    private void DetachEditorTabs()
    {
        CurrentEditorWorkspace.ActiveTabId = _activeEditorTab?.Id;
        EditorContentHost.Children.Clear();
        _vm.Tabs.EditorTabs.Clear();
        _activeEditorTab = null;
    }

    private void AttachEditorTabs()
    {
        EditorContentHost.Children.Clear();
        _vm.Tabs.EditorTabs.Clear();

        foreach (var tab in _editorTabs)
        {
            if (!EditorContentHost.Children.Contains(tab.Control))
                EditorContentHost.Children.Add(tab.Control);

            _vm.Tabs.AddEditorTab(tab.Id, tab.Control.FilePath, tab.Control.IsModified, false);
        }
    }

    private async Task RestoreBrowserTabsAsync(WorkspaceSnapshot workspace)
    {
        var browserWorkspace = GetOrCreateBrowserWorkspace(workspace.Id);
        _activeBrowserWorkspace = browserWorkspace;
        _browserTabs = browserWorkspace.Tabs;

        if (browserWorkspace.IsInitialized && _browserTabs.Count > 0)
        {
            await AttachBrowserTabsAsync();
            ActivateBrowserTab(browserWorkspace.ActiveTabId ?? _browserTabs[0].Id);
            return;
        }

        browserWorkspace.IsInitialized = true;
        var snapshots = workspace.BrowserTabs;
        var tabs = snapshots.Count == 0
            ? new[] { new BrowserTabSnapshot { Url = DefaultBrowserUrl, Title = "Browser", IsActive = true } }
            : snapshots.ToArray();

        foreach (var snapshot in tabs)
            await CreateBrowserTabAsync(
                snapshot.Url ?? DefaultBrowserUrl,
                snapshot.Id == Guid.Empty ? null : snapshot.Id,
                snapshot.Title);

        var active = tabs.FirstOrDefault(t => t.IsActive) ?? tabs.First();
        ActivateBrowserTab(active.Id);
    }

    private BrowserWorkspaceTabs GetOrCreateBrowserWorkspace(Guid workspaceId)
    {
        if (_browserWorkspaces.TryGetValue(workspaceId, out var browserWorkspace))
            return browserWorkspace;

        browserWorkspace = new BrowserWorkspaceTabs();
        _browserWorkspaces[workspaceId] = browserWorkspace;
        return browserWorkspace;
    }

    private void DetachBrowserTabs()
    {
        CurrentBrowserWorkspace.ActiveTabId = _activeBrowserTab?.Id;
        BrowserContentHost.Children.Clear();
        _vm.Tabs.BrowserTabs.Clear();
        _activeBrowserTab = null;
    }

    private async Task AttachBrowserTabsAsync()
    {
        BrowserContentHost.Children.Clear();
        _vm.Tabs.BrowserTabs.Clear();

        foreach (var tab in _browserTabs)
        {
            if (!BrowserContentHost.Children.Contains(tab.View))
                BrowserContentHost.Children.Add(tab.View);

            _vm.Tabs.AddBrowserTab(tab.Id, tab.View.CoreWebView2?.DocumentTitle, false);
            await RefreshBrowserTabIconAsync(tab);
        }
    }

    private void SaveActiveWorkspaceSnapshot()
    {
        if (_isSwitchingWorkspace || _activeWorkspace is null)
            return;

        CaptureInto(_activeWorkspace);
        _vm.Workspaces.SaveSnapshot(_activeWorkspace);
    }

    private void CaptureInto(WorkspaceSnapshot snapshot)
    {
        snapshot.LastUsedUtc = DateTime.UtcNow;
        snapshot.Name = WorkspaceListViewModel.DisplayName(snapshot.RootPath);

        snapshot.TerminalTabs = _terminalTabs.Select(tab => new TerminalTabSnapshot
        {
            Id = tab.Id,
            WorkingDirectory = Directory.Exists(tab.View.WorkingDirectory)
                ? tab.View.WorkingDirectory
                : _terminal.CurrentDirectory,
            Title = tab.View.HeaderTitle,
            IsActive = tab.Id == _activeTerminalTab?.Id
        }).ToList();

        var activeTerminal = _activeTerminalTab?.View ?? _terminalTabs.FirstOrDefault()?.View;
        if (activeTerminal is not null)
        {
            snapshot.Terminal.WorkingDirectory = Directory.Exists(activeTerminal.WorkingDirectory)
                ? activeTerminal.WorkingDirectory
                : _terminal.CurrentDirectory;
            snapshot.Terminal.Title = activeTerminal.HeaderTitle;
        }

        snapshot.EditorTabs = _editorTabs.Select(tab => new EditorTabSnapshot
        {
            Id = tab.Id,
            FilePath = tab.Control.FilePath,
            Text = tab.Control.Text,
            Title = EditorTitle(tab.Control),
            IsModified = tab.Control.IsModified,
            IsActive = tab.Id == _activeEditorTab?.Id
        }).ToList();

        var activeEditor = _activeEditorTab?.Control ?? _editorTabs.FirstOrDefault()?.Control;
        if (activeEditor is not null)
        {
            snapshot.Editor.FilePath = activeEditor.FilePath;
            snapshot.Editor.Text = activeEditor.Text;
            snapshot.Editor.IsModified = activeEditor.IsModified;
        }

        snapshot.BrowserTabs = _browserTabs.Select(tab => new BrowserTabSnapshot
        {
            Id = tab.Id,
            Url = tab.View.Source?.ToString(),
            Title = tab.View.CoreWebView2?.DocumentTitle,
            IsActive = tab.Id == _activeBrowserTab?.Id
        }).ToList();

        CaptureLayoutSizes();
        snapshot.PaneLayout = _root is null ? null : ToSnapshot(_root);
    }

    private void OnClosing(object? sender, CancelEventArgs e) => SaveActiveWorkspaceSnapshot();

    private static string EditorTitle(VimEditorControl editor)
        => string.IsNullOrWhiteSpace(editor.FilePath) ? "Untitled" : Path.GetFileName(editor.FilePath);

    private static string NormalizeBrowserAddress(string text)
    {
        var address = text.Trim();
        if (string.IsNullOrWhiteSpace(address))
            return DefaultBrowserUrl;

        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme))
            return uri.ToString();

        if (address.Contains(' '))
            return $"https://www.google.com/search?q={Uri.EscapeDataString(address)}";

        var scheme = address.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                     || address.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            ? "http://"
            : "https://";

        return scheme + address;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // 最大化/復元アイコンを切り替える。
        MaximizeIcon.Data = maximized ? RestoreGeometry : MaximizeGeometry;
        MaximizeButton.ToolTip = maximized ? "元に戻す" : "最大化";
    }

    // ===== 最大化時にタスクバーを覆わないようワーク領域へ制限する（WindowStyle=None 対策） =====
    //
    // WindowStyle="None" のボーダレスウィンドウは、最大化するとモニタ全体（タスクバー含む）に
    // 広がってしまい、最下部の AI バーがタスクバーの裏に隠れる。WM_GETMINMAXINFO を処理して
    // 最大化サイズをモニタのワーク領域（タスクバーを除いた範囲）に収める。

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var work = monitorInfo.rcWork;
                    var mon = monitorInfo.rcMonitor;
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    // 最大化位置とサイズをワーク領域基準（モニタ左上からの相対）に設定する。
                    mmi.ptMaxPosition.X = work.Left - mon.Left;
                    mmi.ptMaxPosition.Y = work.Top - mon.Top;
                    mmi.ptMaxSize.X = work.Right - work.Left;
                    mmi.ptMaxSize.Y = work.Bottom - work.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
