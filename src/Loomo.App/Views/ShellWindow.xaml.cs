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
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;
using Editor.Controls;
using Editor.Controls.Git;
using Editor.Controls.Themes;
using Terminal.Settings;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow : Window
{
    private readonly TerminalService _terminal;
    private readonly EditorService _editor;
    private readonly BrowserService _browser;
    private readonly IWorkspaceService _workspace;
    private readonly TabIconService _tabIcons;
    private readonly AiSettings _settings;
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
    private EditorTab? _markdownPreviewSourceTab;
    private BrowserTab? _markdownPreviewBrowserTab;
    private DispatcherTimer? _markdownPreviewDebounceTimer;
    private WebView2CompositionControl? _markdownPreviewEventsView;

    /// <summary>
    /// WebView2 のユーザーデータフォルダ（Cookie・保存パスワード・サイト権限の保存先）。
    /// 既定だと実行ファイル隣に作られ再ビルドで消えるため、%APPDATA%/Loomo 配下に固定して
    /// パスワード自動保存やフォルダ等の権限許可をセッションをまたいで永続化する。
    /// </summary>
    private static readonly string WebViewUserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "WebView2");
    private bool _syncingMarkdownPreviewFromEditor;
    private bool _syncingEditorFromMarkdownPreview;
    private bool _markdownPreviewScrollSyncQueued;
    private double _pendingMarkdownPreviewScrollRatio;
    private BrowserTab? _lastRealActiveBrowserTab;
    private WorkspaceSnapshot? _activeWorkspace;
    private DispatcherOperation? _pendingWorkspaceSnapshotSave;
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

    // ===== ドラッグ中のスナップ風プレビュー =====
    private Popup? _dragPopup;
    private Canvas? _dragCanvas;
    private Border? _dragPreview;       // ドロップ先の半分を塗るプレビュー矩形
    private Border? _dragTargetOutline; // ドロップ先ペイン全体の枠
    private bool _paneDragging;
    private PaneKind _dragSource;
    private PaneKind? _dragTarget;
    private DropZone? _dragZone;

    // ===== ペイン間フォーカス移動（Ctrl+W h/j/k/l） =====
    /// <summary>直近でキーボードフォーカスを得た領域（移動の起点）。ペイン本体またはサイドバー。</summary>
    private FocusTarget? _focusedRegion;
    /// <summary>Ctrl+W プレフィックスを受け取り、次の h/j/k/l を待ち受けている状態。</summary>
    private bool _awaitingPaneDirection;

    /// <summary>フォーカス移動の対象領域：ペイン本体（<see cref="Pane"/> あり）またはサイドバー（null）。</summary>
    private readonly record struct FocusTarget(PaneKind? Pane)
    {
        public bool IsSidebar => Pane is null;
        public static FocusTarget Sidebar => new((PaneKind?)null);
        public static FocusTarget Of(PaneKind kind) => new(kind);
    }

    public ShellWindow(
        ShellViewModel vm,
        TerminalService terminal,
        EditorService editor,
        BrowserService browser,
        IWorkspaceService workspace,
        TabIconService tabIcons,
        AiSettings settings)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        _terminal = terminal;
        _editor = editor;
        _browser = browser;
        _editor.NewVirtualDocumentTabRequested += OpenVirtualDocumentTab;
        _workspace = workspace;
        _tabIcons = tabIcons;
        _settings = settings;
        _terminalTabs = _scratchTerminalWorkspace.Tabs;
        _editorTabs = _scratchEditorWorkspace.Tabs;
        _browserTabs = _scratchBrowserWorkspace.Tabs;

        InitializePanes();

        // サイドバーの開閉に追従して列幅・スプリッターを切り替える
        vm.PropertyChanged += OnShellPropertyChanged;
        vm.Settings.Saved += ApplyVimEnabledToOpenEditorTabs;
        vm.Appearance.AppearanceChanged += ApplyAppearanceToOpenTabs;
        vm.Tabs.TabActivated += OnSidebarTabActivated;
        vm.Tabs.TabCloseRequested += OnSidebarTabCloseRequested;
        vm.Workspaces.WorkspaceActivated += OnWorkspaceActivated;
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        Loaded += OnLoaded;

        // Ctrl+W に続けて h/j/k/l でフォーカスを上下左右の隣接ペインへ移す（vim 風）。
        // Terminal/Editor/AI は WPF コントロールなのでトンネリングの PreviewKeyDown で本体より先に拾う。
        // Browser(WebView2) は内部でキー入力を消費するため、Browser ペインにフォーカスがある間は
        // このナビゲーションが効かない場合がある（既知の制限）。
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
            if (_activeTerminalTab is not { } activeTerminal)
                return;

            if (!string.IsNullOrEmpty(root))
                _terminal.SetWorkingDirectory(root);
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
                // WebView2 の生成は遅延（Browser ペインが見えたら背景で実体化する）。
                CreateBrowserTab(DefaultBrowserUrl);
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

        // Browser ペインが（再）表示されたら、遅延していたアクティブタブの WebView2 を実体化する。
        ScheduleBrowserRealize(_activeBrowserTab);
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

    private GridSplitter NewSplitter(bool cols, Brush border)
    {
        var splitter = new GridSplitter
        {
            Width = cols ? 4 : double.NaN,
            Height = cols ? double.NaN : 4,
            ResizeDirection = cols ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = border
        };
        splitter.DragCompleted += (_, _) => SaveActiveWorkspaceSnapshot();
        return splitter;
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
                AddFlattenedChildren(kids, inner); // 同方向は比率を保ってフラット化
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

    private static void AddFlattenedChildren(List<PaneNode> destination, PaneSplit inner)
    {
        var total = inner.Children.Sum(c => c.Weight > 0 ? c.Weight : 1);
        var scale = total > 0 ? (inner.Weight > 0 ? inner.Weight : 1) / total : 1;
        foreach (var child in inner.Children)
        {
            var weight = child.Weight > 0 ? child.Weight : 1;
            child.Weight = weight * scale;
            destination.Add(child);
        }
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
    /// その上でマウスを追跡＆プレビューを描画する。
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
            if (_focusedRegion?.Pane == kind)
                _focusedRegion = null; // 起点が消えたので次回ナビゲーションは可視ペインから選び直す
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

        if (e.NewFocus is not DependencyObject d)
            return;
        if (FindPaneOf(d) is { } kind)
            _focusedRegion = FocusTarget.Of(kind);
        else if (IsWithin(d, SidebarContainer))
            _focusedRegion = FocusTarget.Sidebar;
    }

    /// <summary>要素が指定の祖先（論理・視覚いずれか）の内側にあるか。</summary>
    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = GetAnyParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
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

    /// <summary>
    /// 起点領域から指定方向で最も近い隣接領域へフォーカスを移す。候補にはペイン本体に加え、
    /// 表示中ならサイドバー（Explorer 等）も含めるので、最左ペインから Ctrl+W h でサイドバーへ移れる。
    /// </summary>
    private void FocusPaneInDirection(DropZone direction)
    {
        var targets = FocusTargets().ToList();
        if (targets.Count == 0)
            return;

        // 起点：直近フォーカスの領域。見つからなければ最初の候補（=可視ペイン）を起点扱いにする。
        var originIndex = _focusedRegion is { } region
            ? targets.FindIndex(t => t.Target == region)
            : -1;
        if (originIndex < 0)
            originIndex = 0;
        var (originTarget, from) = targets[originIndex];

        var fromCenter = new Point(from.X + from.Width / 2, from.Y + from.Height / 2);
        FocusTarget? best = null;
        var bestScore = double.MaxValue;

        foreach (var (target, r) in targets)
        {
            if (target == originTarget)
                continue;

            // 指定方向の側にある領域だけを候補にする（タイル配置なので辺で判定）。
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
            // 移動軸方向の距離を主に、直交方向のずれを従にして最も近い領域を選ぶ。
            var (axis, perpendicular) = direction is DropZone.Left or DropZone.Right
                ? (Math.Abs(center.X - fromCenter.X), Math.Abs(center.Y - fromCenter.Y))
                : (Math.Abs(center.Y - fromCenter.Y), Math.Abs(center.X - fromCenter.X));
            var score = axis + perpendicular * 2;
            if (score < bestScore)
            {
                bestScore = score;
                best = target;
            }
        }

        if (best is { } target2)
            ApplyFocusTarget(target2);
    }

    /// <summary>ナビゲーション候補（表示中ペイン＋サイドバー）を矩形付きで列挙する。ペインを先頭に並べる。</summary>
    private IEnumerable<(FocusTarget Target, Rect Rect)> FocusTargets()
    {
        foreach (var leaf in AllLeaves())
            if (!leaf.Hidden && TryGetPaneRect(leaf.Kind, out var rect))
                yield return (FocusTarget.Of(leaf.Kind), rect);

        if (TryGetSidebarRect(out var sidebarRect))
            yield return (FocusTarget.Sidebar, sidebarRect);
    }

    /// <summary>サイドバーの矩形（PaneHost 座標系）を取得する。非表示・未配置なら false。</summary>
    private bool TryGetSidebarRect(out Rect rect)
    {
        rect = default;
        if (!_vm.IsSidebarVisible || !SidebarContainer.IsVisible
            || SidebarContainer.ActualWidth <= 0 || SidebarContainer.ActualHeight <= 0)
            return false;

        // サイドバーは PaneHost の左隣にあるため X は負になるが、辺判定・距離計算はそのまま成立する。
        var topLeft = SidebarContainer.TransformToVisual(PaneHost).Transform(new Point(0, 0));
        rect = new Rect(topLeft, new Size(SidebarContainer.ActualWidth, SidebarContainer.ActualHeight));
        return true;
    }

    private void ApplyFocusTarget(FocusTarget target)
    {
        if (target.IsSidebar)
            FocusSidebar();
        else
            FocusPane(target.Pane!.Value);
    }

    /// <summary>表示中のサイドバー（Explorer 等）へキーボードフォーカスを移す。</summary>
    private void FocusSidebar()
    {
        if (!_vm.IsSidebarVisible)
            return;

        var view = SidebarContainer.Children.OfType<UIElement>()
            .FirstOrDefault(c => c.Visibility == Visibility.Visible);
        if (view is null)
            return;

        _focusedRegion = FocusTarget.Sidebar;
        if (view is FolderTreeView tree)
            tree.FocusTree();           // Explorer は中身のツリーへ直接フォーカス（先頭未選択なら選ぶ）
        else
            FocusFirstFocusable(view);  // 他パネルは最初のフォーカス可能要素へ
    }

    /// <summary>要素ツリーを深さ優先でたどり、最初のフォーカス可能要素へフォーカスを移す。</summary>
    private static bool FocusFirstFocusable(DependencyObject root)
    {
        if (root is UIElement { Focusable: true, IsVisible: true, IsEnabled: true } element)
        {
            element.Focus();
            return true;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FocusFirstFocusable(VisualTreeHelper.GetChild(root, i)))
                return true;
        return false;
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
        _focusedRegion = FocusTarget.Of(kind);
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
        ApplyTerminalAppearance(view);
        var tab = new TerminalTab(requestedId ?? Guid.NewGuid(), view);
        view.HeaderTitleChanged += (_, title) => UpdateTerminalTab(tab, title);
        return tab;
    }

    private EditorTab CreateEditorTab(Guid? requestedId = null)
    {
        // GitServiceFactory を渡すと、エディタが行の差分（追加/変更/削除）をガター（行番号脇）に
        // マーク表示し、ステータスバーにブランチ名を出す。読込/保存/編集のたびに自動で再計算される
        // （RefreshGitDiff はコントロール内部で発火）。未指定だと NullEditorGitService となり無効。
        var control = new VimEditorControl(new VimEditorControlOptions
        {
            GitServiceFactory = () => new GitDiffProvider()
        })
        {
            VimEnabled = _settings.Vim.Enabled,
            Visibility = Visibility.Collapsed
        };
        ApplyEditorAppearance(control);
        var tab = new EditorTab(requestedId ?? Guid.NewGuid(), control);
        control.BufferChanged += (_, _) =>
        {
            UpdateEditorTab(tab);
            if (ReferenceEquals(_markdownPreviewSourceTab, tab))
                ScheduleMarkdownPreviewUpdate();
        };
        control.SaveRequested += (_, _) =>
        {
            QueueEditorTabUpdate(tab);
            if (ReferenceEquals(_markdownPreviewSourceTab, tab))
                ScheduleMarkdownPreviewUpdate();
        };
        control.MarkdownPreviewRequested += async (_, _) => await OpenMarkdownPreviewAsync(tab);
        return tab;
    }

    private void ApplyVimEnabledToOpenEditorTabs()
    {
        foreach (var tab in _editorTabs)
            tab.Control.VimEnabled = _settings.Vim.Enabled;
    }

    /// <summary>
    /// エディタの配色は設定で選んだプリセット（<see cref="AppearanceSettings.EditorTheme"/>）を
    /// ベースにしつつ、選択ハイライトだけを Loomo のアクセント色（半透明）へ差し替える。
    /// 既定の選択色は暗い背景に埋もれて見えないため。<see cref="EditorTheme"/> は複製手段が無いので、
    /// リフレクションで全プロパティを写し取り <c>SelectionBg</c> だけ上書きする
    /// （ライブラリ側がパレットを更新しても追従でき、Loomo 側に色定義が漏れない）。
    /// </summary>
    private EditorTheme BuildEditorTheme()
    {
        var accent = (Application.Current?.TryFindResource("Accent") as SolidColorBrush)?.Color
                     ?? Color.FromRgb(0x61, 0x48, 0xDE);
        var selection = new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B));

        var baseTheme = ResolveEditorTheme(_settings.Appearance.EditorTheme);
        var clone = new EditorTheme();
        foreach (var prop in typeof(EditorTheme).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;
            var value = prop.Name == nameof(EditorTheme.SelectionBg) ? selection : prop.GetValue(baseTheme);
            prop.SetValue(clone, value);
        }
        return clone;
    }

    /// <summary>設定のテーマ名から <see cref="EditorTheme"/> の組み込みプリセットを解決する。未知名は Dracula。</summary>
    private static EditorTheme ResolveEditorTheme(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "dark" => EditorTheme.Dark,
        "nord" => EditorTheme.Nord,
        "tokyonight" => EditorTheme.TokyoNight,
        "onedark" => EditorTheme.OneDark,
        _ => EditorTheme.Dracula,
    };

    /// <summary>エディタへ配色テーマとフォント（設定値、未指定なら触らない）を適用する。</summary>
    private void ApplyEditorAppearance(VimEditorControl control)
    {
        control.SetTheme(BuildEditorTheme());
        var ap = _settings.Appearance;
        if (!string.IsNullOrWhiteSpace(ap.EditorFontFamily))
            control.FontFamily = new FontFamily(ap.EditorFontFamily);
        if (ap.EditorFontSize > 0)
            control.FontSize = ap.EditorFontSize;
    }

    /// <summary>ターミナルへ配色（背景/文字色/ANSIパレット）とフォントを適用する。
    /// sk0ya.Terminal.Controls 1.0.5 の <see cref="TerminalTabView.SetColorTheme"/> / <see cref="TerminalTabView.SetFont"/>
    /// を使う。WPF の <c>Background</c>/<c>FontFamily</c> を直接書いても描画サーフェスには届かないため、
    /// 必ずこの専用APIを経由する。フォントは未指定なら現状値を保つ。</summary>
    private void ApplyTerminalAppearance(TerminalTabView view)
    {
        var ap = _settings.Appearance;
        view.SetColorTheme(BuildTerminalColorTheme(ap.TerminalTheme));

        var family = string.IsNullOrWhiteSpace(ap.TerminalFontFamily)
            ? view.FontFamilyName
            : ap.TerminalFontFamily;
        var size = ap.TerminalFontSize > 0 ? ap.TerminalFontSize : view.TerminalFontSize;
        view.SetFont(family, size);
    }

    /// <summary>ターミナル配色プリセット名 → <see cref="TerminalColorTheme"/>（背景/文字色/16色ANSIパレット）。
    /// 外観パネルの代表色（背景/文字色）と一致させる。未知名は Dark。</summary>
    private static TerminalColorTheme BuildTerminalColorTheme(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "light" => MakeTerminalTheme("#1F1F1F", "#FFFFFF", LightAnsiPalette, "#1F1F1F", "#FFB3D7FF"),
        "dracula" => MakeTerminalTheme("#F8F8F2", "#282A36", DraculaAnsiPalette, "#F8F8F0", "#6644475A"),
        "nord" => MakeTerminalTheme("#D8DEE9", "#2E3440", NordAnsiPalette, "#D8DEE9", "#66434C5E"),
        "solarizeddark" => MakeTerminalTheme("#93A1A1", "#002B36", SolarizedDarkAnsiPalette, "#93A1A1", "#66073642"),
        _ => MakeTerminalTheme("#D4D4D4", "#1E1E1E", DarkAnsiPalette, "#5FAFFF", "#664D4D4D"),
    };

    private static TerminalColorTheme MakeTerminalTheme(
        string fg, string bg, string[] ansiPalette, string cursor, string selection) =>
        new(
            ParseColor(fg),
            ParseColor(bg),
            ansiPalette.Select(ParseColor).ToArray(),
            ParseColor(cursor),
            ParseColor(selection));

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    // 16色ANSIパレット（0-7=標準, 8-15=明色）。
    private static readonly string[] DarkAnsiPalette =
    {
        "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
        "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2",
    };

    private static readonly string[] LightAnsiPalette =
    {
        "#000000", "#C50F1F", "#13A10E", "#B58900", "#0037DA", "#881798", "#3A96DD", "#777777",
        "#5A5A5A", "#A4262C", "#0E8016", "#986801", "#0037DA", "#A100A1", "#178C92", "#1F1F1F",
    };

    private static readonly string[] DraculaAnsiPalette =
    {
        "#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
        "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF",
    };

    private static readonly string[] NordAnsiPalette =
    {
        "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
        "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4",
    };

    private static readonly string[] SolarizedDarkAnsiPalette =
    {
        "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
        "#002B36", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3",
    };

    /// <summary>外観設定の変更を、開いている全エディタ／ターミナルタブと Markdown プレビューへ即時反映する。</summary>
    private void ApplyAppearanceToOpenTabs()
    {
        foreach (var tab in _editorTabs)
            ApplyEditorAppearance(tab.Control);
        foreach (var tab in _terminalTabs)
            ApplyTerminalAppearance(tab.View);
        if (_markdownPreviewSourceTab is not null)
            ScheduleMarkdownPreviewUpdate();
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

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>直近に ActivityBar をクリックした時刻（TickCount64）。ダブルクリック自前判定用。</summary>
    private long _lastActivityBarClickTick;

    /// <summary>
    /// ActivityBar の空き領域をドラッグするとウィンドウを移動する（タイトルバーと同じ操作感）。
    /// アイコンボタン上のクリックはボタン側で処理済みのためここへは伝播しない。
    /// ダブルクリックは最大化／元に戻すをトグルする。
    /// </summary>
    private void OnActivityBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // ネイティブのキャプション移動ループに入ると WPF の ClickCount による
        // ダブルクリック判定が成立しないため、クリック間隔から自前で判定する。
        var now = Environment.TickCount64;
        var isDoubleClick = now - _lastActivityBarClickTick <= GetDoubleClickTime();
        _lastActivityBarClickTick = isDoubleClick ? 0 : now;

        if (isDoubleClick)
        {
            OnMaximizeRestore(sender, e);
            return;
        }

        // 最大化中はドラッグで動かさない（WindowChrome のタイトルバー側と挙動を揃える）。
        if (WindowState != WindowState.Normal)
            return;

        // DragMove() は WPF 経由でカクつくため、タイトルバー(WindowChrome)と同じ
        // ネイティブのキャプション移動ループ（HTCAPTION）へ委ねて滑らかに動かす。
        var hwnd = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

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

    // タブを中ボタンクリックで閉じる（Terminal / Editor / Browser 共通）
    private async void OnTabMiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not FrameworkElement { Tag: Guid id })
            return;

        e.Handled = true;
        if (_terminalTabs.Any(t => t.Id == id))
            await CloseTerminalTabAsync(id);
        else if (_editorTabs.Any(t => t.Id == id))
            CloseEditorTab(id);
        else if (_browserTabs.Any(t => t.Id == id))
            await CloseBrowserTabAsync(id);
        else
            return;

        SaveActiveWorkspaceSnapshot();
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

    /// <summary>
    /// 仮想ドキュメント（システムプロンプト・危険コマンド一覧など）を編集するための専用タブを用意する。
    /// 同名タブが既にあればそれをアクティブ化して再利用し、無ければ新規タブを作成する。
    /// EditorService が <see cref="VimEditorControl.OpenVirtualDocument"/> を呼ぶ直前にこれを呼ぶため、
    /// ここでアクティブ化（＝Attach）した control に対して仮想ドキュメントが開かれる。
    /// </summary>
    private void OpenVirtualDocumentTab(string title)
    {
        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.VirtualTitle, title, StringComparison.Ordinal));
        if (existing is not null)
        {
            ActivateEditorTab(existing.Id);
            return;
        }

        var tab = CreateEditorTab();
        tab.VirtualTitle = title;
        _editorTabs.Add(tab);
        EditorContentHost.Children.Add(tab.Control);
        _vm.Tabs.AddEditorTab(tab.Id, title, false, false);
        ActivateEditorTab(tab.Id);
        SaveActiveWorkspaceSnapshot();
    }

    private async Task OpenFileInNewEditorTabAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var existing = _editorTabs.FirstOrDefault(t =>
            string.Equals(t.Control.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateEditorTab(existing.Id);
            if (_markdownPreviewBrowserTab is not null)
                await SwitchMarkdownPreviewSourceAsync(existing);
            return;
        }

        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        EditorContentHost.Children.Add(tab.Control);
        _vm.Tabs.AddEditorTab(tab.Id, path, false, false);
        ActivateEditorTab(tab.Id);
        await _editor.OpenFileAsync(path);
        UpdateEditorTab(tab);
        if (_markdownPreviewBrowserTab is not null)
            await SwitchMarkdownPreviewSourceAsync(tab);
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

    private async void OnMarkdownPreview(object sender, RoutedEventArgs e)
    {
        if (_activeEditorTab is not null)
            await OpenMarkdownPreviewAsync(_activeEditorTab);
    }

    private async Task OpenMarkdownPreviewAsync(EditorTab sourceTab)
    {
        if (_markdownPreviewBrowserTab is null || !_browserTabs.Contains(_markdownPreviewBrowserTab))
        {
            _markdownPreviewBrowserTab = await CreateBrowserTabAsync(
                "about:blank",
                requestedTitle: "Markdown Preview",
                isMarkdownPreview: true);
        }
        else
        {
            ActivateBrowserTab(_markdownPreviewBrowserTab.Id);
        }

        await SwitchMarkdownPreviewSourceAsync(sourceTab);
    }

    private async Task SwitchMarkdownPreviewSourceAsync(EditorTab sourceTab)
    {
        if (!ReferenceEquals(_markdownPreviewSourceTab, sourceTab))
        {
            if (_markdownPreviewSourceTab is not null)
                _markdownPreviewSourceTab.Control.ViewportScrolled -= MarkdownPreviewSource_ViewportScrolled;

            _markdownPreviewSourceTab = sourceTab;
            sourceTab.Control.ViewportScrolled += MarkdownPreviewSource_ViewportScrolled;
        }

        await UpdateMarkdownPreviewAsync();
    }

    private void ScheduleMarkdownPreviewUpdate()
    {
        if (_markdownPreviewSourceTab is null || _markdownPreviewBrowserTab is null)
            return;

        if (_markdownPreviewDebounceTimer is null)
        {
            _markdownPreviewDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _markdownPreviewDebounceTimer.Tick += async (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                await UpdateMarkdownPreviewAsync();
            };
        }

        _markdownPreviewDebounceTimer.Stop();
        _markdownPreviewDebounceTimer.Start();
    }

    private async Task UpdateMarkdownPreviewAsync()
    {
        var source = _markdownPreviewSourceTab;
        var preview = _markdownPreviewBrowserTab;
        if (source is null || preview is null || !_browserTabs.Contains(preview))
            return;

        try
        {
            await preview.View.EnsureCoreWebView2Async();
            AttachMarkdownPreviewWebViewEvents(preview.View);
        }
        catch
        {
            return;
        }

        var filePath = source.Control.FilePath;
        var ext = filePath is null ? string.Empty : Path.GetExtension(filePath).ToLowerInvariant();
        var title = filePath is null ? "Markdown Preview" : $"Preview: {Path.GetFileName(filePath)}";
        string html;

        var previewStyle = _settings.Appearance.MarkdownPreviewTheme;
        if (ext is ".md" or ".markdown")
        {
            html = MarkdownRenderer.RenderToHtml(source.Control.Text, title, previewStyle);
        }
        else
        {
            html = MarkdownRenderer.RenderToHtml(
                "## Markdown Preview\n\nActive editor tab is not a Markdown file.",
                "Markdown Preview",
                previewStyle);
        }

        preview.View.CoreWebView2!.NavigateToString(html);
        _vm.Tabs.UpdateBrowserTab(preview.Id, title);
        if (ReferenceEquals(_activeBrowserTab, preview))
            BrowserAddressBox.Text = title;
    }

    private void AttachMarkdownPreviewWebViewEvents(WebView2CompositionControl view)
    {
        if (ReferenceEquals(_markdownPreviewEventsView, view))
            return;

        DetachMarkdownPreviewWebViewEvents();
        if (view.CoreWebView2 is null)
            return;

        view.CoreWebView2.WebMessageReceived += MarkdownPreview_WebMessageReceived;
        _markdownPreviewEventsView = view;
    }

    private void DetachMarkdownPreviewWebViewEvents()
    {
        if (_markdownPreviewEventsView?.CoreWebView2 is not null)
            _markdownPreviewEventsView.CoreWebView2.WebMessageReceived -= MarkdownPreview_WebMessageReceived;
        _markdownPreviewEventsView = null;
    }

    private void DetachMarkdownPreviewSource()
    {
        if (_markdownPreviewSourceTab is not null)
            _markdownPreviewSourceTab.Control.ViewportScrolled -= MarkdownPreviewSource_ViewportScrolled;
        _markdownPreviewSourceTab = null;
    }

    private async void MarkdownPreviewSource_ViewportScrolled(object? sender, EventArgs e)
    {
        if (_syncingEditorFromMarkdownPreview || sender is not VimEditorControl editor)
            return;

        await QueueMarkdownPreviewScrollSyncAsync(editor.VerticalScrollRatio);
    }

    private void MarkdownPreview_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_syncingMarkdownPreviewFromEditor || _markdownPreviewSourceTab is null)
            return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || type.GetString() != "markdownPreviewScroll"
                || !root.TryGetProperty("ratio", out var ratioElement)
                || !ratioElement.TryGetDouble(out var ratio))
                return;

            _syncingEditorFromMarkdownPreview = true;
            _markdownPreviewSourceTab.Control.ScrollToVerticalRatio(ratio);
        }
        catch
        {
            // Ignore malformed messages from preview content.
        }
        finally
        {
            _syncingEditorFromMarkdownPreview = false;
        }
    }

    private async Task QueueMarkdownPreviewScrollSyncAsync(double ratio)
    {
        _pendingMarkdownPreviewScrollRatio = Math.Clamp(ratio, 0.0, 1.0);
        if (_markdownPreviewScrollSyncQueued)
            return;

        _markdownPreviewScrollSyncQueued = true;
        try
        {
            while (_markdownPreviewBrowserTab is not null)
            {
                var nextRatio = _pendingMarkdownPreviewScrollRatio;
                await ScrollMarkdownPreviewToRatioAsync(nextRatio);

                if (Math.Abs(nextRatio - _pendingMarkdownPreviewScrollRatio) < 0.0001)
                    break;
            }
        }
        finally
        {
            _markdownPreviewScrollSyncQueued = false;
        }
    }

    private async Task ScrollMarkdownPreviewToRatioAsync(double ratio)
    {
        var view = _markdownPreviewBrowserTab?.View;
        if (view?.CoreWebView2 is null)
            return;

        _syncingMarkdownPreviewFromEditor = true;
        try
        {
            var script = FormattableString.Invariant(
                $"window.setMarkdownPreviewScrollRatio && window.setMarkdownPreviewScrollRatio({Math.Clamp(ratio, 0.0, 1.0):R});");
            await view.ExecuteScriptAsync(script);
        }
        catch
        {
            // Best effort: the preview can be navigating while the editor scrolls.
        }
        finally
        {
            _syncingMarkdownPreviewFromEditor = false;
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
        if (sender is not WebView2CompositionControl view)
            return;

        var tab = _browserTabs.FirstOrDefault(t => ReferenceEquals(t.View, view));
        if (tab is null)
            return;

        UpdateBrowserTab(tab);
        _ = RefreshBrowserTabIconAsync(tab);
        if (ReferenceEquals(_activeBrowserTab, tab))
        {
            if (tab.IsMarkdownPreview)
            {
                var docTitle = tab.View.CoreWebView2?.DocumentTitle;
                BrowserAddressBox.Text = string.IsNullOrEmpty(docTitle) ? "Markdown Preview" : docTitle;
            }
            else
            {
                BrowserAddressBox.Text = view.Source?.ToString() ?? string.Empty;
            }
        }

        if (tab.IsMarkdownPreview && _markdownPreviewSourceTab is not null)
            _ = QueueMarkdownPreviewScrollSyncAsync(_markdownPreviewSourceTab.Control.VerticalScrollRatio);
    }

    private async void NavigateBrowser(string text)
    {
        var address = NormalizeBrowserAddress(text);
        BrowserAddressBox.Text = address;

        if (_activeBrowserTab is not { } tab)
            return;

        // 未実体化なら、この URL を保留先にして実体化（＝そのままナビゲート）する。
        tab.PendingUrl = address;
        await EnsureBrowserRealizedAsync(tab);

        // 既に実体化済みだった場合は PendingUrl が消費されないので、明示的にナビゲートする。
        if (tab.View.CoreWebView2 is { } core && tab.PendingUrl is not null)
        {
            tab.PendingUrl = null;
            core.Navigate(address);
        }
        UpdateBrowserTab(tab);
        SaveActiveWorkspaceSnapshot();
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
        if (_markdownPreviewBrowserTab is not null)
            _ = SwitchMarkdownPreviewSourceAsync(tab);
        SaveActiveWorkspaceSnapshot();
    }

    private void CloseEditorTab(Guid id)
    {
        var index = _editorTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;

        var wasActive = _activeEditorTab?.Id == id;
        var tab = _editorTabs[index];
        if (ReferenceEquals(_markdownPreviewSourceTab, tab))
        {
            _markdownPreviewDebounceTimer?.Stop();
            DetachMarkdownPreviewSource();
        }
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

    private WebView2CompositionControl? ActiveBrowserView => _activeBrowserTab?.View;

    private BrowserWorkspaceTabs CurrentBrowserWorkspace
        => _activeBrowserWorkspace ?? _scratchBrowserWorkspace;

    /// <summary>
    /// ブラウザタブを生成して即座に WebView2 まで実体化する（markdown プレビューや新規タブなど、
    /// 直後に CoreWebView2 を使う呼び出し向け）。起動経路は <see cref="CreateBrowserTab"/>（遅延）を使う。
    /// </summary>
    private async Task<BrowserTab> CreateBrowserTabAsync(
        string url,
        Guid? requestedId = null,
        string? requestedTitle = null,
        bool isMarkdownPreview = false)
    {
        var tab = CreateBrowserTab(url, requestedId, requestedTitle, isMarkdownPreview);
        await EnsureBrowserRealizedAsync(tab);
        return tab;
    }

    /// <summary>
    /// ブラウザタブの器（WebView2 コントロール・タブUI）だけを同期で用意し、<b>CoreWebView2 の生成は遅延</b>する。
    /// 重い <c>EnsureCoreWebView2Async</c> を起動の臨界パスから外すのが狙い。実体化は Browser ペインが
    /// 見えてアクティブになった時（<see cref="ScheduleBrowserRealize"/>）に背景優先度で行う。
    /// </summary>
    private BrowserTab CreateBrowserTab(
        string url,
        Guid? requestedId = null,
        string? requestedTitle = null,
        bool isMarkdownPreview = false)
    {
        var id = requestedId ?? Guid.NewGuid();
        var browserWorkspace = CurrentBrowserWorkspace;
        var view = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
            Visibility = Visibility.Collapsed,
            // 全タブで同じユーザーデータフォルダを共有 → Cookie・保存パスワード・サイト権限が
            // タブ間で共通になり、再ビルド・再起動をまたいで残る。
            CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = WebViewUserDataFolder }
        };
        view.NavigationCompleted += OnBrowserNavigationCompleted;

        // markdown プレビューは UpdateMarkdownPreviewAsync が内容を流し込むので初期ナビゲートは不要。
        var tab = new BrowserTab(id, view, isMarkdownPreview)
        {
            PendingUrl = isMarkdownPreview ? null : NormalizeBrowserAddress(url)
        };
        _browserTabs.Add(tab);
        BrowserContentHost.Children.Add(view);
        _vm.Tabs.AddBrowserTab(id, requestedTitle ?? $"Tab {browserWorkspace.NextTabNumber++}", false);
        ActivateBrowserTab(id);
        return tab;
    }

    /// <summary>
    /// タブの CoreWebView2 を生成し、保留中の URL があればナビゲートする（冪等・多重生成防止）。
    /// </summary>
    private async Task EnsureBrowserRealizedAsync(BrowserTab tab)
    {
        if (tab.RealizationStarted)
            return;
        tab.RealizationStarted = true;
        try
        {
            await tab.View.EnsureCoreWebView2Async();
        }
        catch
        {
            tab.RealizationStarted = false;   // 失敗時は次回の表示・操作で再試行できるようにする
            return;
        }

        ConfigureBrowserCore(tab.View.CoreWebView2!);
        tab.View.CoreWebView2!.FaviconChanged += OnBrowserFaviconChanged;
        if (tab.PendingUrl is { } pending)
        {
            tab.PendingUrl = null;
            tab.View.Source = new Uri(pending);
        }
        UpdateBrowserTab(tab);
        await RefreshBrowserTabIconAsync(tab);
    }

    /// <summary>
    /// 実体化した CoreWebView2 を通常ブラウザらしく設定する：パスワードの自動保存・自動入力を有効化し、
    /// サイト権限（フォルダ/ファイルアクセス・通知・位置情報など）の許可/拒否をプロファイルへ保存させる。
    /// 永続化先は <see cref="WebViewUserDataFolder"/>。
    /// </summary>
    private static void ConfigureBrowserCore(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsPasswordAutosaveEnabled = true;   // 既定 false：これが無いと保存プロンプトすら出ない
        settings.IsGeneralAutofillEnabled = true;    // 住所など一般フォームの自動入力

        core.PermissionRequested += OnBrowserPermissionRequested;
    }

    /// <summary>
    /// サイト権限リクエストの扱い。原則は既定UI（許可/拒否ダイアログ）に任せつつ、ユーザーの選択を
    /// プロファイルへ保存して次回以降は再確認しないようにする（<see cref="CoreWebView2PermissionRequestedEventArgs.SavesInProfile"/>）。
    ///
    /// ただし File System Access API（フォルダ/ファイルの読み書き許可）は Chromium が原則セッション
    /// 限りでしか権限を保持しないため、<c>SavesInProfile</c> を立てても起動のたびに再確認される。
    /// dev ツール用途として、この権限だけは自動的に許可してプロンプトを抑止する。
    /// </summary>
    private static void OnBrowserPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.SavesInProfile = true;

        if (e.PermissionKind == CoreWebView2PermissionKind.FileReadWrite)
            e.State = CoreWebView2PermissionState.Allow;
    }

    /// <summary>
    /// Browser ペインが表示中なら、アクティブなブラウザタブの WebView2 実体化を背景優先度で予約する。
    /// 起動・レイアウト変更の臨界パスをブロックしないよう <see cref="DispatcherPriority.Background"/> で遅延実行する。
    /// </summary>
    private void ScheduleBrowserRealize(BrowserTab? tab)
    {
        if (tab is null || tab.RealizationStarted || !IsPaneVisible(PaneKind.Browser))
            return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                // 予約後に別タブへ切替・ペイン非表示になっていたら実体化しない。
                if (ReferenceEquals(_activeBrowserTab, tab) && IsPaneVisible(PaneKind.Browser))
                    _ = EnsureBrowserRealizedAsync(tab);
            }));
    }

    private async Task CloseBrowserTabAsync(Guid id)
    {
        var index = _browserTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;

        var wasActive = _activeBrowserTab?.Id == id;
        var tab = _browserTabs[index];
        if (ReferenceEquals(_markdownPreviewBrowserTab, tab))
        {
            _markdownPreviewDebounceTimer?.Stop();
            DetachMarkdownPreviewWebViewEvents();
            DetachMarkdownPreviewSource();
            _markdownPreviewBrowserTab = null;
        }
        if (ReferenceEquals(_lastRealActiveBrowserTab, tab))
            _lastRealActiveBrowserTab = null;
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
        if (!tab.IsMarkdownPreview)
            _lastRealActiveBrowserTab = tab;
        CurrentBrowserWorkspace.ActiveTabId = id;
        // AIのブラウザ操作（IBrowserService）の対象を、いま見えているタブへ一本化する。
        _browser.SetActiveView(tab.View);
        _vm.Tabs.ActivateBrowserTab(id);
        // 未実体化のタブは Source が null なので、保留中の遷移先 URL を表示する。
        BrowserAddressBox.Text = tab.View.Source?.ToString() ?? tab.PendingUrl ?? string.Empty;
        tab.View.Focus();
        // Browser ペインが見えていれば、このタブの WebView2 を背景で実体化する。
        ScheduleBrowserRealize(tab);
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
    /// <summary><see cref="VirtualTitle"/> は仮想ドキュメント（設定の長文項目など）を開いたタブの表示名。
    /// 仮想ドキュメントは FilePath を持たないため、タブ名はこの値から決める（通常ファイルは null）。</summary>
    private sealed record EditorTab(Guid Id, VimEditorControl Control)
    {
        public string? VirtualTitle { get; set; }
    }
    private sealed record BrowserTab(Guid Id, WebView2CompositionControl View, bool IsMarkdownPreview = false)
    {
        /// <summary>まだ CoreWebView2 を生成していない間の遷移先 URL（実体化時にここへナビゲートする）。
        /// 起動を速くするため Browser ペインが見えるまで WebView2 生成を遅らせる。markdown プレビューは null。</summary>
        public string? PendingUrl { get; set; }

        /// <summary>CoreWebView2 の生成を開始済みか（多重生成・多重ナビゲートの防止）。</summary>
        public bool RealizationStarted { get; set; }
    }

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
        // 仮想ドキュメントは FilePath を持たないため、タブ名は VirtualTitle から決める。
        var title = tab.Control.IsVirtualDocument && !string.IsNullOrEmpty(tab.VirtualTitle)
            ? tab.VirtualTitle
            : tab.Control.FilePath;
        _vm.Tabs.UpdateEditorTab(tab.Id, title, tab.Control.IsModified);
        SaveActiveWorkspaceSnapshot();
    }

    private async void OnWorkspaceActivated(object? sender, WorkspaceSnapshot workspace)
        => await SwitchWorkspaceAsync(workspace, captureCurrent: true);

    private async Task SwitchWorkspaceAsync(WorkspaceSnapshot workspace, bool captureCurrent)
    {
        if (captureCurrent)
            SaveActiveWorkspaceSnapshot(immediate: true);

        DetachTerminalTabs();
        DetachEditorTabs();
        DetachBrowserTabs();
        _activeWorkspace = workspace;
        _vm.FolderTree.LoadRoot(workspace.RootPath);
        ApplyPaneLayout(workspace.PaneLayout);

        RestoreTerminalTabs(workspace);
        RestoreEditorTabs(workspace);
        await RestoreBrowserTabsAsync(workspace);

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

        // WebView2 の生成は遅延。アクティブなタブだけが、ペイン表示時に背景で実体化される。
        foreach (var snapshot in tabs)
            CreateBrowserTab(
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
        DiscardMarkdownPreviewTab();
        _lastRealActiveBrowserTab = null;
        CurrentBrowserWorkspace.ActiveTabId = _activeBrowserTab?.Id;
        BrowserContentHost.Children.Clear();
        _vm.Tabs.BrowserTabs.Clear();
        _activeBrowserTab = null;
        _browser.SetActiveView(null);
    }

    // マークダウンプレビューはワークスペースをまたいで保持しない。切替時に破棄し、ソースエディタの
    // ViewportScrolled 購読も解除して、別ワークスペースに stale な参照（とそれ経由のスクロール同期や
    // 再アタッチ時に蘇る空プレビュータブ）が残らないようにする。
    private void DiscardMarkdownPreviewTab()
    {
        var preview = _markdownPreviewBrowserTab;
        _markdownPreviewDebounceTimer?.Stop();
        DetachMarkdownPreviewWebViewEvents();
        DetachMarkdownPreviewSource();
        _markdownPreviewBrowserTab = null;
        if (preview is null)
            return;

        if (ReferenceEquals(_activeBrowserTab, preview))
            _activeBrowserTab = null;

        _browserTabs.Remove(preview);
        if (preview.View.CoreWebView2 is not null)
            preview.View.CoreWebView2.FaviconChanged -= OnBrowserFaviconChanged;
        BrowserContentHost.Children.Remove(preview.View);
        preview.View.NavigationCompleted -= OnBrowserNavigationCompleted;
        preview.View.Dispose();
        _vm.Tabs.RemoveBrowserTab(preview.Id);
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

    private void SaveActiveWorkspaceSnapshot(bool immediate = false)
    {
        if (_activeWorkspace is null)
            return;

        if (immediate)
        {
            _pendingWorkspaceSnapshotSave?.Abort();
            _pendingWorkspaceSnapshotSave = null;
            SaveActiveWorkspaceSnapshotNow();
            return;
        }

        if (_pendingWorkspaceSnapshotSave is { Status: DispatcherOperationStatus.Pending })
            return;

        _pendingWorkspaceSnapshotSave = Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _pendingWorkspaceSnapshotSave = null;
                SaveActiveWorkspaceSnapshotNow();
            }),
            DispatcherPriority.ApplicationIdle);
    }

    private void SaveActiveWorkspaceSnapshotNow()
    {
        if (_activeWorkspace is null)
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

        // 仮想ドキュメント（システムプロンプト等の編集タブ）は永続化しない。FilePath を持たず、
        // 復元しても設定への保存コールバックが失われた「Untitled」タブになってしまうため。
        var persistableEditorTabs = _editorTabs.Where(tab => !tab.Control.IsVirtualDocument).ToList();
        snapshot.EditorTabs = persistableEditorTabs.Select(tab => new EditorTabSnapshot
        {
            Id = tab.Id,
            FilePath = tab.Control.FilePath,
            Text = tab.Control.Text,
            Title = EditorTitle(tab.Control),
            IsModified = tab.Control.IsModified,
            IsActive = tab.Id == _activeEditorTab?.Id
        }).ToList();

        var activeEditor = persistableEditorTabs.FirstOrDefault(t => t.Id == _activeEditorTab?.Id)?.Control
            ?? persistableEditorTabs.FirstOrDefault()?.Control;
        if (activeEditor is not null)
        {
            snapshot.Editor.FilePath = activeEditor.FilePath;
            snapshot.Editor.Text = activeEditor.Text;
            snapshot.Editor.IsModified = activeEditor.IsModified;
        }

        // プレビュータブは永続化しないので、それがアクティブなときは最後に見ていた実タブを
        // アクティブ扱いにして、復元時に選択が失われないようにする。
        var activeRealBrowserId = (_activeBrowserTab is { IsMarkdownPreview: false }
            ? _activeBrowserTab
            : _lastRealActiveBrowserTab)?.Id;
        snapshot.BrowserTabs = _browserTabs.Where(tab => !tab.IsMarkdownPreview).Select(tab => new BrowserTabSnapshot
        {
            Id = tab.Id,
            Url = tab.View.Source?.ToString(),
            Title = tab.View.CoreWebView2?.DocumentTitle,
            IsActive = tab.Id == activeRealBrowserId
        }).ToList();

        CaptureLayoutSizes();
        snapshot.PaneLayout = _root is null ? null : ToSnapshot(_root);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
        => SaveActiveWorkspaceSnapshot(immediate: true);

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
