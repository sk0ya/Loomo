using System.Threading;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel
{
    private async void ScheduleFilter(string query)
    {
        _filterCts?.Cancel();

        if (_multiRootStates.Count > 0)
        {
            await ScheduleMultiRootFilterAsync(query);
            return;
        }

        if (_currentRoot is null) return;

        if (string.IsNullOrEmpty(query))
        {
            _filterCts = null;
            ReloadNodes();
            FilterCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        var cts = new CancellationTokenSource();
        _filterCts = cts;
        var token = cts.Token;
        var root = _currentRoot;

        try
        {
            await Task.Delay(160, token);
            var built = await Task.Run(() => BuildFilteredTree(root, root, token), token);
            token.ThrowIfCancellationRequested();
            Nodes.Clear();
            foreach (var node in built) Nodes.Add(node);
            HasVisibleNodes = Nodes.Count > 0;
            EmptyMessage = CreateEmptyMessage();
            FilterCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (ReferenceEquals(_filterCts, cts)) _filterCts = null;
            cts.Dispose();
        }
    }

    // マルチルート時：Nodes（フォルダー見出し）自体はフィルタしない。フォルダーごとに見出しの
    // Children をフィルタ結果へ差し替える／クエリが空になったら通常の一覧へ戻す。
    private async Task ScheduleMultiRootFilterAsync(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            _filterCts = null;
            foreach (var state in _multiRootStates.Values)
                ReconcileRootStateChildren(state);
            FilterCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        var cts = new CancellationTokenSource();
        _filterCts = cts;
        var token = cts.Token;
        var states = _multiRootStates.Values.ToList();

        try
        {
            await Task.Delay(160, token);
            foreach (var state in states)
            {
                token.ThrowIfCancellationRequested();
                if (state.HeaderNode is not { } header)
                    continue;

                var built = await Task.Run(
                    () => BuildFilteredTree(state.DisplayedPath, state.FolderPath, token), token);
                token.ThrowIfCancellationRequested();
                header.LoadChildren(built);
                header.IsExpanded = true;
            }
            FilterCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (ReferenceEquals(_filterCts, cts)) _filterCts = null;
            cts.Dispose();
        }
    }

    private List<FileNodeViewModel> BuildFilteredTree(string root, string rootKey, CancellationToken token)
    {
        var state = ResolveGitState(rootKey);
        var entries = FolderTreeFilter.BuildFilteredTree(
            root, MatchesFilter,
            (path, isDirectory, ignored) => ShouldShow(path, isDirectory, ignored, state),
            state.GetIgnoredPaths,
            computeIgnored: HideIgnoredFiles && !ShowChangedOnly, token);
        return entries.Select(e => ToNode(e, rootKey)).ToList();
    }

    private FileNodeViewModel ToNode(FolderTreeFilter.Entry entry, string rootKey)
    {
        var node = new FileNodeViewModel(entry.FullPath, entry.IsDirectory, this, rootKey);
        if (entry.IsReparseLeaf)
            node.LoadChildren(Array.Empty<FileNodeViewModel>());
        else if (entry.IsDirectory)
        {
            node.LoadChildren(entry.Children.Select(e => ToNode(e, rootKey)).ToList());
            node.IsExpanded = true;
        }
        return node;
    }
}
