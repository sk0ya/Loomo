using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

public sealed record TodoEntry(string Tag, ContentSearchHit Hit)
{
    public bool IsExpanded { get; set; }
    public string Label => $"{Hit.Line}: {Hit.LineText.Trim()}";
    public string ToolTip => $"{Hit.FullPath}:{Hit.Line}";
}

public sealed partial class TodoGroup : ObservableObject
{
    public TodoGroup(string name, IReadOnlyList<TodoEntry> entries) { Name = name; Entries = entries; }
    public string Name { get; }
    public IReadOnlyList<TodoEntry> Entries { get; }
    public string Label => $"{Name} ({Entries.Count})";
    [ObservableProperty] private bool _isExpanded = true;
}

/// <summary>既存の全文検索を再利用する TODO 一覧。検索世代を照合し、古い部屋の結果を採用しない。</summary>
public sealed partial class TodoTreeViewModel : ObservableObject, IDisposable
{
    public const string TagPattern = @"\b(TODO|FIXME|HACK|NOTE)\b";
    public const int ResultLimit = 2000;
    private static readonly Regex Tags = new(TagPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly IWorkspaceSearchService _search;
    private readonly IWorkspaceService _workspace;
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<TodoEntry> _entries = [];
    private bool _truncated;
    private bool _disposed;
    public TodoTreeViewModel(IWorkspaceSearchService search, IWorkspaceService workspace)
    { _search = search; _workspace = workspace; }
    public ObservableCollection<TodoGroup> Groups { get; } = [];
    public event EventHandler<ContentSearchHit>? OpenRequested;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _excludeGlob = "";
    [ObservableProperty] private bool _groupByTag;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "TODO / FIXME / HACK / NOTE を検索します";
    public IWorkspaceService Workspace => _workspace;
    partial void OnFilterChanged(string value) => RebuildGroups();
    partial void OnGroupByTagChanged(bool value) => RebuildGroups();
    partial void OnExcludeGlobChanged(string value) => Invalidate();
    public event EventHandler? Invalidated;
    public void Invalidate()
    {
        CancelSearch();
        _entries = [];
        _truncated = false;
        RebuildGroups();
        Invalidated?.Invoke(this, EventArgs.Empty);
    }
    public void CancelSearch() => _searchCancellation?.Cancel();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAsync()
    {
        if (_disposed) return;
        CancelSearch();
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        IsBusy = true;
        Status = "検索中…";
        try
        {
            var options = new GrepOptions(CaseSensitive: true, UseRegex: true,
                ExcludeGlob: string.IsNullOrWhiteSpace(ExcludeGlob) ? null : ExcludeGlob.Trim(), MaxResults: ResultLimit + 1);
            // rg が無い環境の走査も UI スレッドを塞がない。
            var hits = await Task.Run(() => _search.GrepAsync(TagPattern, options, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || _disposed) return;
            _truncated = hits.Count > ResultLimit;
            _entries = Parse(hits.Take(ResultLimit));
            RebuildGroups();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested) Status = $"検索できませんでした: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            { _searchCancellation = null; IsBusy = false; }
        }
    }

    public static IReadOnlyList<TodoEntry> Parse(IEnumerable<ContentSearchHit> hits)
        => hits.SelectMany(hit => Tags.Matches(hit.LineText).Cast<Match>()
            .Select(match => new TodoEntry(match.Value, hit with { Column = match.Index + 1 })))
            .DistinctBy(entry => (entry.Hit.FullPath, entry.Hit.Line, entry.Hit.Column))
            .ToList();

    private void RebuildGroups()
    {
        var expanded = Groups.ToDictionary(g => g.Name, g => g.IsExpanded);
        var filter = Filter.Trim();
        var entries = _entries.Where(e => filter.Length == 0 || e.Hit.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || e.Hit.LineText.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        Groups.Clear();
        foreach (var group in entries.GroupBy(e => GroupByTag ? e.Tag : e.Hit.RelativePath)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            Groups.Add(new TodoGroup(group.Key, group.OrderBy(e => e.Hit.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Hit.Line).ThenBy(e => e.Hit.Column).ToList())
                { IsExpanded = !expanded.TryGetValue(group.Key, out var value) || value });
        Status = _workspace.Folders.Count == 0 ? "フォルダーを開くと TODO を表示します"
            : entries.Count == 0 ? "該当する TODO はありません"
            : $"{entries.Count} 件 / {entries.Select(e => e.Hit.FullPath).Distinct().Count()} ファイル";
        if (_truncated) Status += $"（先頭 {ResultLimit} 行まで。除外条件で絞り込めます）";
    }
    [RelayCommand] private void Open(TodoEntry? entry)
    { if (entry is not null) OpenRequested?.Invoke(this, entry.Hit); }
    public void Dispose() { _disposed = true; CancelSearch(); }
}
