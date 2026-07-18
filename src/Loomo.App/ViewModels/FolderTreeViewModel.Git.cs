using System.Threading;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel
{
    private void RefreshGitStateAsync()
    {
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
            var state = await Task.Run(() => GitTreeState.Load(root, token), token);
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

    private void ApplyFilterStatus(GitTreeState state)
    {
        var filters = new List<string>();
        if (HideIgnoredFiles) filters.Add("ignore 非表示");
        if (ShowChangedOnly) filters.Add("変更のみ");
        var gitStatus = state.IsGitRepository ? $"{state.ChangedFiles.Count} 件変更" : "Git 未検出";
        FilterStatus = filters.Count == 0 ? gitStatus : $"{string.Join(" / ", filters)} - {gitStatus}";
    }
}
