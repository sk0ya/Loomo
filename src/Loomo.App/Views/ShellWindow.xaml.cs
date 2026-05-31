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
        _terminalTabs = _scratchTerminalWorkspace.Tabs;
        _editorTabs = _scratchEditorWorkspace.Tabs;
        _browserTabs = _scratchBrowserWorkspace.Tabs;

        // サイドバーの開閉に追従して列幅・スプリッターを切り替える
        vm.PropertyChanged += OnShellPropertyChanged;
        vm.Tabs.TabActivated += OnSidebarTabActivated;
        vm.Workspaces.WorkspaceActivated += OnWorkspaceActivated;
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        Loaded += OnLoaded;

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

    private TerminalTab CreateTerminalTab(string startDirectory, Guid? requestedId = null)
    {
        var view = new TerminalTabView("powershell.exe", startDirectory);
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
            AttachBrowserTabs();
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

    private void AttachBrowserTabs()
    {
        BrowserContentHost.Children.Clear();
        _vm.Tabs.BrowserTabs.Clear();

        foreach (var tab in _browserTabs)
        {
            if (!BrowserContentHost.Children.Contains(tab.View))
                BrowserContentHost.Children.Add(tab.View);

            _vm.Tabs.AddBrowserTab(tab.Id, tab.View.CoreWebView2?.DocumentTitle, false);
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
