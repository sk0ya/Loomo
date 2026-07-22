using System.Text.RegularExpressions;
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

    /// <summary>1ファイル内の <paramref name="query"/> の全一致を <paramref name="replacement"/> へ置換して
    /// 書き戻す。ディスク上の現在の内容を読み直してから置換する（検索実行時点の LineText はキャッシュなので、
    /// その後の編集を踏まえて安全に反映するため）。一致が0件ならファイルには触れない。実際に置換した件数を返す。
    /// 不正な正規表現（<paramref name="useRegex"/> 時）は何もせず0を返す。</summary>
    public int ReplaceInFile(string fullPath, string query, string replacement, bool caseSensitive, bool useRegex)
    {
        string text;
        try { text = File.ReadAllText(fullPath); }
        catch { return 0; }

        var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        var pattern = useRegex ? query : Regex.Escape(query);
        Regex regex;
        try { regex = new Regex(pattern, options); }
        catch { return 0; }

        var count = regex.Matches(text).Count;
        if (count == 0) return 0;

        // 正規表現モードは置換文字列の $1 等の後方参照を活かす。リテラルモードは MatchEvaluator を使い、
        // 置換文字列に $ が含まれていても特殊解釈させない（そのまま挿入する）。
        var result = useRegex ? regex.Replace(text, replacement) : regex.Replace(text, _ => replacement);
        File.WriteAllText(fullPath, result);
        return count;
    }
}
