using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
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
    private readonly ShellViewModel _vm;
    private readonly Dictionary<Guid, TerminalTabView> _terminalViews = new();
    private readonly List<BrowserTab> _browserTabs = new();
    private BrowserTab? _activeBrowserTab;
    private int _browserTabNumber = 1;
    private WorkspaceSnapshot? _activeWorkspace;
    private bool _isSwitchingWorkspace;
    private bool _clearEditorSnapshotOnNextCapture;
    private const string DefaultBrowserUrl = "https://www.google.com/";

    /// <summary>サイドバーを閉じる直前の幅を保持し、再表示時に復元する。</summary>
    private GridLength _savedSidebarWidth = new(220);

    public ShellWindow(
        ShellViewModel vm,
        TerminalService terminal,
        EditorService editor,
        IWorkspaceService workspace)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        _terminal = terminal;
        _editor = editor;
        _workspace = workspace;

        // サイドバーの開閉に追従して列幅・スプリッターを切り替える
        vm.PropertyChanged += OnShellPropertyChanged;
        vm.Tabs.TabActivated += OnSidebarTabActivated;
        vm.Workspaces.WorkspaceActivated += OnWorkspaceActivated;
        vm.FolderTree.FolderOpenRequested += (_, path) => vm.Workspaces.ActivateFolder(path);
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        Loaded += OnLoaded;

        var startDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // sk0ya コントロールを生成してホストへ配置し、サービスへ結びつける
        var termView = CreateTerminalView(startDir);
        ShowTerminalView(termView);
        _terminal.SetWorkingDirectory(startDir);
        UpdateTerminalTab(termView, termView.HeaderTitle);

        var editorCtrl = new VimEditorControl();
        EditorHost.Child = editorCtrl;
        _editor.Attach(editorCtrl);
        editorCtrl.BufferChanged += (_, _) => UpdateEditorTab(editorCtrl);
        editorCtrl.SaveRequested += (_, _) => UpdateEditorTab(editorCtrl);
        UpdateEditorTab(editorCtrl);

        // フォルダを開いたらエージェントの作業ディレクトリを同期
        _workspace.RootChanged += (_, root) =>
        {
            if (!_isSwitchingWorkspace && !string.IsNullOrEmpty(root))
                _terminal.SetWorkingDirectory(root);
            if (TerminalHost.Child is TerminalTabView activeTerminal)
                UpdateTerminalTab(activeTerminal, activeTerminal.HeaderTitle);
        };

        // ファイル選択でエディタに開く
        _workspace.SelectionChanged += async (_, path) =>
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                await _editor.OpenFileAsync(path);
                UpdateEditorTab(editorCtrl);
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm.Workspaces.ActiveWorkspace is { } workspace)
                await SwitchWorkspaceAsync(workspace, captureCurrent: false);
            else
            {
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

    private TerminalTabView CreateTerminalView(string startDirectory)
    {
        var view = new TerminalTabView("powershell.exe", startDirectory);
        view.HeaderTitleChanged += (_, title) => UpdateTerminalTab(view, title);
        return view;
    }

    private void ShowTerminalView(TerminalTabView view)
    {
        TerminalHost.Child = view;
        _terminal.Attach(view);
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

    private WebView2? ActiveBrowserView => _activeBrowserTab?.View;

    private async Task CreateBrowserTabAsync(string url, Guid? requestedId = null, string? requestedTitle = null)
    {
        var id = requestedId ?? Guid.NewGuid();
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
        _vm.Tabs.AddBrowserTab(id, requestedTitle ?? $"Tab {_browserTabNumber++}", false);
        ActivateBrowserTab(id);

        await view.EnsureCoreWebView2Async();
        view.Source = new Uri(normalizedUrl);
        UpdateBrowserTab(tab);
    }

    private async Task CloseBrowserTabAsync(Guid id)
    {
        var index = _browserTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return;

        var wasActive = _activeBrowserTab?.Id == id;
        var tab = _browserTabs[index];
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

    private sealed record BrowserTab(Guid Id, WebView2 View);

    private void OnSidebarTabActivated(object? sender, TabEntryViewModel tab)
    {
        switch (tab.Kind)
        {
            case TabEntryKind.Terminal:
                if (TerminalHost.Child is TerminalTabView terminal)
                    terminal.FocusTerminal();
                break;
            case TabEntryKind.Editor:
                if (EditorHost.Child is VimEditorControl editor)
                    editor.Focus();
                break;
            case TabEntryKind.Browser:
                ActivateBrowserTab(tab.Id);
                break;
        }
    }

    private void UpdateTerminalTab(TerminalTabView view, string? title)
    {
        _vm.Tabs.SetTerminalSnapshot(title, view.WorkingDirectory);
        SaveActiveWorkspaceSnapshot();
    }

    private void UpdateEditorTab(VimEditorControl ctrl)
    {
        _vm.Tabs.SetEditorSnapshot(ctrl.FilePath, ctrl.IsModified);
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
            _activeWorkspace = workspace;
            var terminal = GetOrCreateTerminalView(workspace);
            ShowTerminalView(terminal);
            _vm.FolderTree.LoadRoot(workspace.RootPath);

            var cwd = workspace.Terminal.WorkingDirectory;
            _terminal.SetWorkingDirectory(Directory.Exists(cwd) ? cwd! : workspace.RootPath);
            UpdateTerminalTab(terminal, workspace.Terminal.Title ?? terminal.HeaderTitle);

            if (EditorHost.Child is VimEditorControl editor)
                _clearEditorSnapshotOnNextCapture = !RestoreEditor(editor, workspace.Editor);

            await RestoreBrowserTabsAsync(workspace.BrowserTabs);
        }
        finally
        {
            _isSwitchingWorkspace = false;
        }

        SaveActiveWorkspaceSnapshot();
    }

    private static bool RestoreEditor(VimEditorControl editor, EditorSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.FilePath) && File.Exists(snapshot.FilePath))
        {
            editor.LoadFile(snapshot.FilePath);
            if (!snapshot.IsModified)
                return true;
        }

        if (snapshot.IsModified || string.IsNullOrWhiteSpace(snapshot.FilePath))
        {
            editor.SetText(snapshot.Text ?? string.Empty);
            return true;
        }

        editor.SetText(string.Empty);
        return false;
    }

    private TerminalTabView GetOrCreateTerminalView(WorkspaceSnapshot workspace)
    {
        if (_terminalViews.TryGetValue(workspace.Id, out var existing))
            return existing;

        var cwd = workspace.Terminal.WorkingDirectory;
        var startDirectory = Directory.Exists(cwd) ? cwd! : workspace.RootPath;
        var view = CreateTerminalView(startDirectory);
        _terminalViews[workspace.Id] = view;
        return view;
    }

    private async Task RestoreBrowserTabsAsync(IReadOnlyList<BrowserTabSnapshot> snapshots)
    {
        ClearBrowserTabs();

        var tabs = snapshots.Count == 0
            ? new[] { new BrowserTabSnapshot { Url = DefaultBrowserUrl, Title = "Browser", IsActive = true } }
            : snapshots;

        foreach (var snapshot in tabs)
            await CreateBrowserTabAsync(
                snapshot.Url ?? DefaultBrowserUrl,
                snapshot.Id == Guid.Empty ? null : snapshot.Id,
                snapshot.Title);

        var active = tabs.FirstOrDefault(t => t.IsActive) ?? tabs.First();
        ActivateBrowserTab(active.Id);
    }

    private void ClearBrowserTabs()
    {
        foreach (var tab in _browserTabs)
        {
            tab.View.NavigationCompleted -= OnBrowserNavigationCompleted;
            tab.View.Dispose();
        }

        _browserTabs.Clear();
        BrowserContentHost.Children.Clear();
        _vm.Tabs.BrowserTabs.Clear();
        _activeBrowserTab = null;
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

        if (TerminalHost.Child is TerminalTabView terminal)
        {
            snapshot.Terminal.WorkingDirectory = Directory.Exists(terminal.WorkingDirectory)
                ? terminal.WorkingDirectory
                : _terminal.CurrentDirectory;
            snapshot.Terminal.Title = terminal.HeaderTitle;
        }

        if (EditorHost.Child is VimEditorControl editor)
        {
            if (_clearEditorSnapshotOnNextCapture)
            {
                snapshot.Editor = new EditorSnapshot();
                _clearEditorSnapshotOnNextCapture = false;
            }
            else
            {
                snapshot.Editor.FilePath = editor.FilePath;
                snapshot.Editor.Text = editor.Text;
                snapshot.Editor.IsModified = editor.IsModified;
            }
        }

        snapshot.BrowserTabs = _browserTabs.Select(tab => new BrowserTabSnapshot
        {
            Id = tab.Id,
            Url = tab.View.Source?.ToString(),
            Title = tab.View.CoreWebView2?.DocumentTitle,
            IsActive = tab.Id == _activeBrowserTab?.Id
        }).ToList();
    }

    private void OnClosing(object? sender, CancelEventArgs e) => SaveActiveWorkspaceSnapshot();

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
