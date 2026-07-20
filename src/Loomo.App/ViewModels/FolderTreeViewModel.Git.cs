using System.Threading;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel
{
    private void RefreshGitStateAsync()
    {
        if (_multiRootStates.Count > 0)
        {
            FilterStatus = _multiRootStates.Count == 1 ? "" : $"{_multiRootStates.Count} フォルダー";
            foreach (var state in _multiRootStates.Values)
                RefreshRootState(state);
            _gitLoadTask = Task.WhenAll(_multiRootStates.Values.Select(s => s.GitLoadTask));
            return;
        }

        _gitLoadCts?.Cancel();
        _gitState = GitTreeState.Empty;
        if (_currentRoot is null)
        {
            _gitLoadCts = null;
            FilterStatus = "";
            ReloadNodes();
            _gitLoadTask = Task.CompletedTask;
            return;
        }

        var cts = new CancellationTokenSource();
        _gitLoadCts = cts;
        _gitLoadTask = LoadGitStateAndReloadAsync(_currentRoot, cts);
    }

    private async Task LoadGitStateAndReloadAsync(string root, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            var state = await Task.Run(() => _query.LoadGitState(root, token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_gitLoadCts, cts) || _currentRoot is null || !PathsEqual(_currentRoot, root))
                return;
            _gitState = state;
            ApplyFilterStatus(state);
            ReloadNodes();
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (ReferenceEquals(_gitLoadCts, cts)) _gitLoadCts = null;
            cts.Dispose();
        }
    }

    // ===== マルチルート：フォルダーごとの Git 状態読込 =====
    // 単一フォルダー時の _gitLoadCts/_gitLoadTask/LoadGitStateAndReloadAsync と同じ形を、
    // フォルダー1件ぶんの状態（FolderTreeRootState）単位で独立に行う。ある1フォルダーの監視・
    // 再読込が他のフォルダーのツリー・展開状態・フォーカスへ波及しない。

    private void RefreshRootState(FolderTreeRootState state)
    {
        state.GitLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        state.GitLoadCts = cts;
        state.GitLoadTask = LoadRootStateAndReconcileAsync(state, cts);
    }

    private async Task LoadRootStateAndReconcileAsync(FolderTreeRootState state, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            var loaded = await Task.Run(() => _query.LoadGitState(state.DisplayedPath, token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(state.GitLoadCts, cts))
                return;
            state.GitState = loaded;
            ReconcileRootStateChildren(state);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (ReferenceEquals(state.GitLoadCts, cts)) state.GitLoadCts = null;
            cts.Dispose();
        }
    }

    // 見出しノードの Children を最新の Git 状態で反映する。初回（未展開）は IsExpanded を立てて
    // 既存の遅延読込経路（FileNodeViewModel.OnIsExpandedChanged）に乗せ、2回目以降（既に展開済み）
    // は ReconcileChildren で差分反映する（展開・フォーカス状態を保つ）。
    private void ReconcileRootStateChildren(FolderTreeRootState state)
    {
        if (state.HeaderNode is not { } header)
            return;

        if (!header.IsExpanded)
            header.IsExpanded = true;
        else
            ReconcileChildren(header.Children, state.DisplayedPath, state.FolderPath);
    }

    private void ApplyFilterStatus(GitTreeState state)
    {
        var filters = new List<string>();
        if (HideIgnoredFiles) filters.Add("ignore 非表示");
        if (ShowChangedOnly) filters.Add("変更のみ");
        var gitStatus = state.IsGitRepository ? $"{state.ChangedFiles.Count} 件変更" : "Git 未検出";
        FilterStatus = filters.Count == 0 ? gitStatus : $"{string.Join(" / ", filters)} - {gitStatus}";
    }
}
