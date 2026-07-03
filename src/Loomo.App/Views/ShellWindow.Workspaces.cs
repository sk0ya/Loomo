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

    private void OnSidebarTabActivated(object? sender, TabEntryViewModel tab)
    {
        switch (tab.Kind)
        {
            case TabEntryKind.Terminal:
                // そのタブのペインがレイアウトに出ていなければ左上と入れ替えて見えるようにする。
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
                ActivateTerminalTab(tab.Id);
                break;
            case TabEntryKind.Editor:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
                ActivateEditorTab(tab.Id);
                break;
            case TabEntryKind.Browser:
                EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
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

        // エディタは LSP（言語サーバープロセス）とファイル監視を抱えるので、実体化済みのものは
        // 明示的に Dispose する。これらは Unloaded では解放されない（一時デタッチでも Unloaded が
        // 発火するため、ライブラリ側で破棄をホスト責務に分離した）。参照を落とすだけだと
        // 言語サーバーが孤児プロセスとして残る。
        if (_editorWorkspaces.Remove(workspaceId, out var editor))
            foreach (var tab in editor.Tabs)
                if (tab.IsRealized)
                    tab.Control.Dispose();
    }

    private void OnCopyWorkspacePathMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: WorkspaceEntryViewModel entry })
            return;

        try { Clipboard.SetText(entry.RootPath); }
        catch { /* クリップボードのロック等は無視 */ }
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
        // 復元による機械的なタブ活性化・ナビゲートを軌跡（操作ログ）へ記録しない。
        var trailSaved = _trailSuppressed;
        _trailSuppressed = true;
        try
        {
            // 軌跡はワークスペース毎（混ざると別ワークスペースのファイルへ飛べて破綻する）。
            _vm.Trail.SetWorkspace(workspace.Id.ToString());
            // ApplicationIdle だけに任せると、起動時のタブ／WebView2 復元が続く間は軌跡バーが
            // 長時間空のままになる。ID が確定したここで読み、以降の復元失敗にも影響されないようにする。
            _vm.Trail.EnsureLoaded();
            _trailLastPane = null;   // ペイン切替のデデュープも新しいワークスペースで仕切り直す
            _trailLastPaneMode = null;
            await SwitchWorkspaceCoreAsync(workspace, captureCurrent, deferHydration);
        }
        finally { _trailSuppressed = trailSaved; }
    }

    private async Task SwitchWorkspaceCoreAsync(WorkspaceSnapshot workspace, bool captureCurrent, bool deferHydration)
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
        // 有効セッション・ステージ・タイル復元より前に IDE ペインの適用可否を確定する。
        ApplyIdePaneApplicability(workspace.RootPath);
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

        // 起動時（deferHydration）は重い復元（ConPTY 起動・エディタ実体化・WebView2）を一気に走らせず、
        // 各段の間でメッセージループへ戻す。これらは団子で繋がると初フレーム後に 1 回の長い UI スレッド
        // ブロック（体感「一瞬カクっと重い」）になるため、フレームを跨ぐ小さなチャンクへ分散して
        // 各段の間に入力/描画を処理させる（実行中のワークスペース切替では従来どおり一括＝体感のため）。
        RestoreTerminalTabs(workspace);
        StartupProfiler.Mark("  復元:RestoreTerminalTabs");
        if (deferHydration)
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        RestoreEditorTabs(workspace);
        StartupProfiler.Mark("  復元:RestoreEditorTabs");
        if (deferHydration)
            await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
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
        // ペイン移動・表示切替・リサイズ・保存レイアウト切替・内部ビューポート分割など、
        // SaveActiveWorkspaceSnapshot を通る全経路で最新の軌跡地点へ配置を反映する。
        RefreshLatestTrailPaneLayout();
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

        // EditorSupport の「ブラウザで開く」が出す一時プレビュー（page.loomo 仮想ホスト）は永続化しない。
        // 書き出し先は一時フォルダで、マップ先の仮想ホストもそのタブの CoreWebView2 限り（再起動後には
        // 張られていない）なので、そのまま復元すると「存在しないファイル」を指す壊れたタブになる。
        snapshot.BrowserTabs = _browserTabs
            .Where(tab => !IsEditorSupportPreviewUrl(tab.View.Source?.ToString()))
            .Select(tab => new BrowserTabSnapshot
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

    /// <summary>終了時に全ワークスペースの実体化済みエディタを Dispose し、LSP（言語サーバープロセス）と
    /// ファイル監視を解放する。Unloaded はこれを行わない（一時デタッチでも発火するため破棄をホスト責務へ
    /// 分離した）ので、明示的に解放しないと言語サーバーが孤児プロセスとして残る。Closed は閉じ確定後に
    /// 1度だけ発火する（Closing と違いキャンセルされない）ので破棄に安全。</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        foreach (var workspace in _editorWorkspaces.Values)
            foreach (var tab in workspace.Tabs)
                if (tab.IsRealized)
                    tab.Control.Dispose();
    }

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
