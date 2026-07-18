using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.Services;

public sealed record SearchPanelResult(IReadOnlyList<object> Roots, string StatusMessage);

/// <summary>ワークスペース検索を実行し、表示可能な結果へ変換する Query。</summary>
public sealed class SearchPanelQuery
{
    private readonly IWorkspaceSearchService _search;
    private readonly SearchResultTreeMapper _mapper;

    public SearchPanelQuery(IWorkspaceSearchService search, SearchResultTreeMapper mapper)
    {
        _search = search;
        _mapper = mapper;
    }

    public async Task<SearchPanelResult> GrepAsync(string query, bool caseSensitive, bool useRegex,
        string? includeGlob, string? excludeGlob, string? searchRoot, CancellationToken cancellationToken)
    {
        var options = new GrepOptions(caseSensitive, useRegex, includeGlob, excludeGlob, 1000);
        var hits = await _search.GrepAsync(query, options, cancellationToken, searchRoot);
        var groups = await Task.Run(() => hits
            .GroupBy(hit => hit.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SearchFileGroup(group.First().FullPath, group.Key,
                group.Select(hit => new SearchMatchItem(hit))))
            .ToList(), cancellationToken);
        var roots = await Task.Run(() => _mapper.Map(groups), cancellationToken);
        var matchCount = groups.Sum(group => group.Count);
        var status = matchCount == 0 ? "一致なし" : $"{matchCount} 件 / {groups.Count} ファイル";
        return new SearchPanelResult(roots, status);
    }

    public async Task<SearchPanelResult> FindFilesAsync(string query, string? searchRoot,
        CancellationToken cancellationToken)
    {
        var hits = await _search.FindFilesAsync(query, 500, cancellationToken, searchRoot);
        var groups = await Task.Run(() => hits
            .Select(hit => new SearchFileGroup(hit.FullPath, hit.RelativePath, Array.Empty<SearchMatchItem>()))
            .ToList(), cancellationToken);
        var roots = await Task.Run(() => _mapper.Map(groups), cancellationToken);
        var status = groups.Count == 0 ? "一致なし" : $"{groups.Count} ファイル";
        return new SearchPanelResult(roots, status);
    }
}
