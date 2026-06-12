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
/// <summary>ShellWindow: ワークスペース切替とスナップショット保存・復元（タブ実体の付け替え）</summary>
public partial class ShellWindow
{
    private sealed record TerminalTab(Guid Id, TerminalTabView View);
    /// <summary><see cref="VirtualTitle"/> は仮想ドキュメント（設定の長文項目など）を開いたタブの表示名。
    /// 仮想ドキュメントは FilePath を持たないため、タブ名はこの値から決める（通常ファイルは null）。</summary>
    private sealed record EditorTab(Guid Id, VimEditorControl Control)
    {
        public string? VirtualTitle { get; set; }
    }
    private sealed record BrowserTab(Guid Id, WebView2CompositionControl View)
    {
        /// <summary>まだ CoreWebView2 を生成していない間の遷移先 URL（実体化時にここへナビゲートする）。
        /// 起動を速くするため Browser ペインが見えるまで WebView2 生成を遅らせる。</summary>
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
        // プレビュータブは編集された時点で通常タブへ昇格する（次のクリックは新しいプレビューになる）。
        if (ReferenceEquals(_previewEditorTab, tab) && tab.Control.IsModified)
            SetPreviewTab(null);

        // 仮想ドキュメントは FilePath を持たないため、タブ名は VirtualTitle から決める。
        var title = tab.Control.IsVirtualDocument && !string.IsNullOrEmpty(tab.VirtualTitle)
            ? tab.VirtualTitle
            : tab.Control.FilePath;
        _vm.Tabs.UpdateEditorTab(tab.Id, title, tab.Control.IsModified);
        SaveActiveWorkspaceSnapshot();
    }

    private async void OnWorkspaceActivated(object? sender, WorkspaceSnapshot workspace)
        => await SwitchWorkspaceAsync(workspace, captureCurrent: true);

    /// <param name="deferHydration">
    /// true なら、レイアウト（ペイン枠）だけ同期で適用し、重いタブ実体化（端末の ConPTY 起動・
    /// エディタコントロール生成＋ファイル読込＋Git差分）は<b>初フレーム描画後</b>に Background 優先度で行う。
    /// 起動時に使い、ウィンドウを素早く表示してから内容をハイドレートする。
    /// </param>
    private async Task SwitchWorkspaceAsync(WorkspaceSnapshot workspace, bool captureCurrent, bool deferHydration = false)
    {
        if (captureCurrent)
            SaveActiveWorkspaceSnapshot(immediate: true);

        ClearStageModeForWorkspaceSwitch();
        DetachTerminalTabs();
        DetachEditorTabs();
        DetachBrowserTabs();
        _activeWorkspace = workspace;
        _vm.FolderTree.LoadRoot(workspace.RootPath, workspace.PinnedFolders, workspace.TreeRootPath);
        StartupProfiler.Mark("  復元:FolderTree.LoadRoot");
        // コンポーザ本文とペグボードはワークスペース毎（どちらも軽量・同期）。
        RestoreComposer(workspace.ComposerText);
        _vm.Pegboard.LoadItems(workspace.Pegboard);
        PrepareStageSnapshot(workspace.Stage);
        StartupProfiler.Mark("  復元:PrepareStageSnapshot");
        ApplyPaneLayout(workspace.PaneLayout);
        // 跨ぎ最大化中のワークスペース切替：切替先のレイアウトを基準に列振り分けを適用し直す。
        // これをしないと切替先のペインがモニタの継ぎ目を跨いだまま表示され、さらに古い
        // _spanSavedRoot（切替前ワークスペースのレイアウト）が切替先のスナップショットへ
        // 保存されてレイアウトが混入する。
        if (_isSpanMaximized)
            ReapplySpanPaneLayout();
        StartupProfiler.Mark("  復元:ApplyPaneLayout");

        // 起動時は、ここで一旦メッセージループへ戻して初フレームを描画させる。Background 優先度は
        // Render より低いので、空のペイン枠が先に出てから下のタブ実体化が走る（体感の起動を短縮）。
        if (deferHydration)
        {
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            StartupProfiler.Mark("  復元:初フレーム後に継続");
        }

        RestoreTerminalTabs(workspace);
        StartupProfiler.Mark("  復元:RestoreTerminalTabs");
        RestoreEditorTabs(workspace);
        StartupProfiler.Mark("  復元:RestoreEditorTabs");
        await RestoreBrowserTabsAsync(workspace);
        StartupProfiler.Mark("  復元:RestoreBrowserTabs");
        CompleteStageSnapshotRestore();
        StartupProfiler.Mark("  復元:CompleteStageSnapshotRestore");

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
        // 分割木を畳んでコンテンツホストを空に（次ワークスペースは単一ビューポートから再構築）。コントロールは破棄しない。
        _terminalViews?.Reset();
        _vm.Tabs.TerminalTabs.Clear();
        _activeTerminalTab = null;
    }

    private void AttachTerminalTabs()
    {
        _terminalViews?.Reset();
        _vm.Tabs.TerminalTabs.Clear();

        // コントロールの配置は後続の ActivateTerminalTab（→ PaneSplitView.Activate）が行う。ここでは strip のみ復元。
        foreach (var tab in _terminalTabs)
            _vm.Tabs.AddTerminalTab(tab.Id, tab.View.HeaderTitle, false);
    }

    private void DetachEditorTabs()
    {
        // EditorSupport の追従先は別ワークスペースへ持ち越さない（stale な購読・参照を残さない）。
        // 内容は復元後の ActivateEditorTab → SwitchEditorSupportSourceAsync が作り直す。
        _editorSupportDebounceTimer?.Stop();
        DetachEditorSupportSource();
        CurrentEditorWorkspace.ActiveTabId = _activeEditorTab?.Id;
        _editorViews?.Reset();
        _vm.Tabs.EditorTabs.Clear();
        _activeEditorTab = null;
        // プレビュー状態はワークスペースをまたいで持ち越さない（復元後は通常タブとして扱う）。
        _previewEditorTab = null;
    }

    private void AttachEditorTabs()
    {
        _editorViews?.Reset();
        _vm.Tabs.EditorTabs.Clear();

        foreach (var tab in _editorTabs)
            _vm.Tabs.AddEditorTab(tab.Id, tab.Control.FilePath, tab.Control.IsModified, false);
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
        CurrentBrowserWorkspace.ActiveTabId = _activeBrowserTab?.Id;
        BrowserContentHost.Children.Clear();
        _vm.Tabs.BrowserTabs.Clear();
        _activeBrowserTab = null;
        _browser.SetActiveView(null);
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

        snapshot.BrowserTabs = _browserTabs.Select(tab => new BrowserTabSnapshot
        {
            Id = tab.Id,
            Url = tab.View.Source?.ToString(),
            Title = tab.View.CoreWebView2?.DocumentTitle,
            IsActive = tab.Id == _activeBrowserTab?.Id
        }).ToList();

        snapshot.PinnedFolders = _vm.FolderTree.PinnedFolders.ToList();
        snapshot.TreeRootPath = _vm.FolderTree.TreeRootOverride;
        snapshot.ComposerText = CaptureComposerText();
        snapshot.Pegboard = _vm.Pegboard.ToSnapshots();
        snapshot.Stage = new StageSnapshot
        {
            IsActive = _stageActive,
            Pane = _stageActive ? _stagePane : null
        };

        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot)
        {
            // 跨ぎ最大化中の一時レイアウト（モニタ単位の列）は永続化せず、跨ぐ前のレイアウト
            // （跨ぎ中の表示切替・移動は反映済み）を保存する。
            snapshot.PaneLayout = ToSnapshot(savedRoot);
        }
        else
        {
            CaptureLayoutSizes();
            snapshot.PaneLayout = _root is null ? null : ToSnapshot(_root);
        }
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
        // Win+↑ 等で本物の最大化（1モニタ）へ入ったら跨ぎ状態は破棄する（レイアウトも戻す）。
        if (WindowState == WindowState.Maximized && _isSpanMaximized)
            ExitSpanState();
        UpdateMaximizeGlyph();
    }
}
