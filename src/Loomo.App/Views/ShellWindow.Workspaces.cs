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
    /// <summary>エディタタブ。起動を速くするため <see cref="Control"/>（VimEditorControl の生成＋ファイル
    /// 読込＋Git差分）は<b>初回アクセス時に遅延実体化</b>する。復元直後は <see cref="Pending"/> に保存済み
    /// スナップショットだけを持ち、アクティブ化や本文取得で初めて実体化する。未実体化のままでもタブ strip・
    /// 永続化・パス重複判定が壊れないよう、メタ情報は <see cref="PeekFilePath"/> 等で実体化せずに読める。</summary>
    private sealed record EditorTab(Guid Id)
    {
        private VimEditorControl? _control;

        /// <summary>未実体化タブの実体化処理（コントロール生成→<see cref="SetControl"/>→Pending から本文復元）。
        /// <see cref="Control"/> の初回アクセスで呼ばれる。</summary>
        public Action<EditorTab>? Realizer { get; init; }

        /// <summary>未実体化の間だけ保持する保存済みスナップショット（実体化時に消費して null になる）。</summary>
        public EditorTabSnapshot? Pending { get; set; }

        public string? VirtualTitle { get; set; }

        /// <summary>コントロールが既に実体化済みか（実体化せずに判定）。</summary>
        public bool IsRealized => _control is not null;

        /// <summary>実体化処理の途中でコントロールを確定する。Pending 復元（LoadFile→BufferChanged で
        /// <see cref="Control"/> へ再入する）より<b>前</b>に呼ぶことで無限再帰を防ぐ。</summary>
        public void SetControl(VimEditorControl control) => _control = control;

        /// <summary>コントロール。未実体化なら初回アクセスでここで実体化する。</summary>
        public VimEditorControl Control
        {
            get
            {
                if (_control is null)
                    Realizer!(this);
                return _control!;
            }
        }

        /// <summary>実体化せずに読めるファイルパス（実体化済みなら現値、未実体化なら保存値）。</summary>
        public string? PeekFilePath => _control?.FilePath ?? Pending?.FilePath;
        /// <summary>実体化せずに読める変更フラグ。</summary>
        public bool PeekIsModified => _control?.IsModified ?? Pending?.IsModified ?? false;
        /// <summary>実体化せずに読める仮想ドキュメント判定（未実体化タブは常に実ファイル＝false）。</summary>
        public bool PeekIsVirtual => _control?.IsVirtualDocument ?? false;
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

    /// <summary>ワークスペースが一覧から取り除かれたとき、その Id にひも付くキャッシュ済みタブ実体を破棄する。
    /// アクティブなものを取り除いた場合は、VM 側が先に別ワークスペースへ切り替えてからこのイベントを上げるため、
    /// ここに来た時点で対象のタブはデタッチ済み（端末ビューは Reset・ブラウザはホストから除去済み）で安全に破棄できる。</summary>
    private async void OnWorkspaceRemoved(object? sender, Guid workspaceId)
    {
        // 端末は ConPTY プロセスを抱えるので明示的に閉じる。
        if (_terminalWorkspaces.Remove(workspaceId, out var terminal))
        {
            foreach (var tab in terminal.Tabs)
                await tab.View.CloseAsync();
        }

        // ブラウザは WebView2 を破棄する。
        if (_browserWorkspaces.Remove(workspaceId, out var browser))
        {
            foreach (var tab in browser.Tabs)
                tab.View.Dispose();
        }

        // エディタはマネージドコントロールのみ。参照を落とせば GC に任せられる。
        _editorWorkspaces.Remove(workspaceId);
    }

    private void OnDeleteWorkspaceMenuClick(object sender, RoutedEventArgs e)
    {
        // 右クリックされたコンボボックス項目（＝そのワークスペース）がメニューの DataContext。
        if (sender is not MenuItem { DataContext: WorkspaceEntryViewModel entry })
            return;

        if (!_vm.Workspaces.RemoveWorkspaceCommand.CanExecute(entry))
        {
            MessageBox.Show(
                this,
                "最後のワークスペースは削除できません（常に1つは開いている必要があります）。",
                "ワークスペースの削除",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            this,
            $"ワークスペース「{entry.Name}」を一覧から削除しますか？\n" +
            "フォルダ自体は削除されません（タブ・レイアウトの保存状態は失われます）。",
            "ワークスペースの削除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.OK)
            _vm.Workspaces.RemoveWorkspaceCommand.Execute(entry);
    }

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
        // 起動時（deferHydration）はエクスプローラのツリー読込（フォルダ列挙）を初フレーム後へ回す。
        // ツリーは初フレームに含まれるが、空→直後に流し込みでよく、初フレームを ~120ms 早められる。
        // 実行中のワークスペース切替では従来どおり即時読込する（体感のため）。
        if (!deferHydration)
        {
            _vm.FolderTree.LoadRoot(workspace.RootPath, workspace.PinnedFolders, workspace.TreeRootPath);
            StartupProfiler.Mark("  復元:FolderTree.LoadRoot");
        }
        // コンポーザ本文とペグボードはワークスペース毎（どちらも軽量・同期）。
        RestoreComposer(workspace);
        _vm.Pegboard.LoadItems(workspace.Pegboard);
        LoadLayouts(workspace.Layouts, workspace.ScratchLayout, workspace.ActiveLayoutIndex, workspace.LayoutDirty);
        LoadEnabledSessions(workspace.EnabledSessions);
        PrepareStageSnapshot(ResolveSoloMode(workspace), workspace.Stage);
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
            // 初フレーム後にエクスプローラのツリーを流し込む（上で先送りした分）。
            _vm.FolderTree.LoadRoot(workspace.RootPath, workspace.PinnedFolders, workspace.TreeRootPath);
            StartupProfiler.Mark("  復元:FolderTree.LoadRoot（遅延）");
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

    /// <summary>復元時のモード判定。<see cref="WorkspaceSnapshot.Mode"/> が正。null の旧データは
    /// かつてのステージ ON をソロ、それ以外（タイル／旧配置）をレイアウトへ移行する。</summary>
    private static bool ResolveSoloMode(WorkspaceSnapshot ws) => ws.Mode switch
    {
        DisplayMode.Solo => true,
        DisplayMode.Layout => false,
        _ => ws.Stage?.IsActive == true,
    };

    private static void RestoreEditor(VimEditorControl editor, EditorTabSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.FilePath) && File.Exists(snapshot.FilePath))
        {
            editor.LoadFile(snapshot.FilePath);
            if (!snapshot.IsModified)
            {
                RestoreEditorViewState(editor, snapshot);
                return;
            }
        }

        if (snapshot.IsModified || string.IsNullOrWhiteSpace(snapshot.FilePath))
        {
            editor.SetText(snapshot.Text ?? string.Empty);
            RestoreEditorViewState(editor, snapshot);
            return;
        }

        editor.SetText(string.Empty);
    }

    /// <summary>カーソル位置とスクロールを戻す（復元の完全性・§19.5）。カーソルはモデル状態なので
    /// 即時に効く。スクロールはレイアウト後でないと算出できないため Loaded 優先度へ遅延し、
    /// それでも取れない場合はカーソル可視化スクロールに任せるベストエフォート。</summary>
    private static void RestoreEditorViewState(VimEditorControl editor, EditorTabSnapshot snapshot)
    {
        if (snapshot.CaretLine > 0 || snapshot.CaretColumn > 0)
            editor.NavigateTo(snapshot.CaretLine, snapshot.CaretColumn);

        if (snapshot.ScrollRatio is { } ratio and > 0)
            editor.Dispatcher.BeginInvoke(
                new Action(() => editor.ScrollToVerticalRatio(ratio)),
                DispatcherPriority.Loaded);
    }

    /// <summary>1タブを永続化用スナップショットへ写す。実体化済みならコントロールの現状から、未実体化なら
    /// 保存済み <see cref="EditorTab.Pending"/> をそのまま返す（まだ開いていない＝内容は不変なので実体化しない）。
    /// <see cref="EditorTabSnapshot.IsActive"/> だけは現在のアクティブタブで上書きする。</summary>
    private EditorTabSnapshot CaptureEditorTab(EditorTab tab)
    {
        var isActive = tab.Id == _activeEditorTab?.Id;
        if (!tab.IsRealized && tab.Pending is { } p)
        {
            return new EditorTabSnapshot
            {
                Id = tab.Id,
                FilePath = p.FilePath,
                Text = p.Text,
                Title = p.Title,
                IsModified = p.IsModified,
                IsActive = isActive,
                CaretLine = p.CaretLine,
                CaretColumn = p.CaretColumn,
                ScrollRatio = p.ScrollRatio
            };
        }

        var c = tab.Control;
        return new EditorTabSnapshot
        {
            Id = tab.Id,
            FilePath = c.FilePath,
            Text = c.Text,
            Title = EditorTitle(c),
            IsModified = c.IsModified,
            IsActive = isActive,
            CaretLine = c.Caret.Line,
            CaretColumn = c.Caret.Column,
            ScrollRatio = c.VerticalScrollRatio
        };
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

        // タブは未実体化（Pending のみ）で並べる。strip 見出しはスナップショットのメタ情報だけで描けるので
        // コントロール生成は不要。実際の VimEditorControl 生成＋ファイル読込＋Git差分は、下の
        // ActivateEditorTab がアクティブタブ 1 枚だけを実体化し、残りは各タブを開いた時に初めて走る
        // （起動時に全タブ分を作っていたのが重さの主因だった）。
        foreach (var snapshot in snapshots)
        {
            var tab = CreatePendingEditorTab(snapshot);
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
        _editorSupportSourcePinned = false;
        UpdateEditorSupportPinToggle();
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

        // 未実体化タブも strip へ戻す（メタ情報のみで描けるので実体化しない）。
        foreach (var tab in _editorTabs)
            _vm.Tabs.AddEditorTab(tab.Id, tab.PeekFilePath, tab.PeekIsModified, false);
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
        // PeekIsVirtual で判定（未実体化タブを実体化しない）。未実体化タブは常に実ファイルなので除外されない。
        var persistableEditorTabs = _editorTabs.Where(tab => !tab.PeekIsVirtual).ToList();
        snapshot.EditorTabs = persistableEditorTabs.Select(CaptureEditorTab).ToList();

        // 凡例（旧 single-editor）フィールド：アクティブタブの内容を反映する。未実体化なら Pending から。
        var activeTab = persistableEditorTabs.FirstOrDefault(t => t.Id == _activeEditorTab?.Id)
            ?? persistableEditorTabs.FirstOrDefault();
        if (activeTab is not null)
        {
            var s = CaptureEditorTab(activeTab);
            snapshot.Editor.FilePath = s.FilePath;
            snapshot.Editor.Text = s.Text;
            snapshot.Editor.IsModified = s.IsModified;
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
        snapshot.ComposerVisible = IsComposerVisible;
        snapshot.ComposerHeight = CaptureComposerHeight();
        snapshot.Pegboard = _vm.Pegboard.ToSnapshots();
        snapshot.Mode = _stageActive ? DisplayMode.Solo : DisplayMode.Layout;
        snapshot.EnabledSessions = _enabledSessions.ToList();
        snapshot.Stage = new StageSnapshot
        {
            IsActive = _stageActive,
            Pane = _stageActive ? _stagePane : null,
            Overview = _stageActive && _overviewActive
        };
        snapshot.Layouts = _layouts.Select(l => new SavedLayout { Name = l.Name, Tree = l.Tree }).ToList();
        snapshot.ScratchLayout = _scratchLayout;
        snapshot.ActiveLayoutIndex = _activeLayoutIndex;
        snapshot.LayoutDirty = _layoutDirty;

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
