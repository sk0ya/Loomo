using System.Threading;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel
{
    private async void ScheduleFilter(string query)
    {
        _filterCts?.Cancel();
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
            var built = await Task.Run(() => BuildFilteredTree(root, token), token);
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

    private List<FileNodeViewModel> BuildFilteredTree(string root, CancellationToken token)
    {
        var entries = FolderTreeFilter.BuildFilteredTree(
            root, MatchesFilter, ShouldShow, _gitState.GetIgnoredPaths,
            computeIgnored: HideIgnoredFiles && !ShowChangedOnly, token);
        return entries.Select(ToNode).ToList();
    }

    private FileNodeViewModel ToNode(FolderTreeFilter.Entry entry)
    {
        var node = new FileNodeViewModel(entry.FullPath, entry.IsDirectory, this);
        if (entry.IsReparseLeaf)
            node.LoadChildren(Array.Empty<FileNodeViewModel>());
        else if (entry.IsDirectory)
        {
            node.LoadChildren(entry.Children.Select(ToNode).ToList());
            node.IsExpanded = true;
        }
        return node;
    }
}
