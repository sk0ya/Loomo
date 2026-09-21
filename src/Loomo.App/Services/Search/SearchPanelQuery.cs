using System.Text.RegularExpressions;
using System.Windows.Input;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.Services;

public sealed record SearchPanelResult(IReadOnlyList<object> Roots, string StatusMessage);

/// <summary>検索欄へ表示する一致範囲（文字列内オフセット）。描画方式から独立した検索ポリシー。</summary>
public readonly record struct SearchTextMatch(int Start, int Length);

/// <summary>検索結果ツリーの展開と、次に置換する一致の選択規則。</summary>
internal static class SearchResultTreePolicy
{
    public static bool CanToggleExpansion(object? node) => node switch
    {
        SearchFolderNode => true,
        SearchFileGroup { Count: > 0 } => true,
        _ => false,
    };

    public static void SetExpanded(object node, bool expanded)
    {
        switch (node)
        {
            case SearchFolderNode folder:
                folder.IsExpanded = expanded;
                foreach (var child in folder.Children)
                    SetExpanded(child, expanded);
                break;
            case SearchFileGroup group:
                group.IsExpanded = expanded;
                break;
        }
    }

    public static bool PreviewSelection(object? selected,
        Action<SearchMatchItem> previewMatch, Action<SearchFileGroup> previewFile)
        => DispatchSelection(selected, previewMatch, previewFile);

    public static bool ActivateSelection(object? selected,
        Action<SearchMatchItem> activateMatch, Action<SearchFileGroup> activateFile)
        => DispatchSelection(selected, activateMatch, activateFile);

    private static bool DispatchSelection(object? selected,
        Action<SearchMatchItem> onMatch, Action<SearchFileGroup> onFile)
    {
        switch (selected)
        {
            case SearchMatchItem match:
                onMatch(match);
                return true;
            case SearchFileGroup { Count: 0 } group:
                onFile(group);
                return true;
            default:
                return false;
        }
    }

    public static (SearchMatchItem? Target, SearchMatchItem? Next) NextUnreplaced(
        IEnumerable<SearchFileGroup> groups, SearchMatchItem? current)
    {
        var pending = groups.SelectMany(group => group.Matches).Where(match => !match.IsReplaced).ToList();
        if (pending.Count == 0)
            return (null, null);

        var index = current is not null ? pending.IndexOf(current) : -1;
        var targetIndex = index >= 0 ? index : 0;
        var nextIndex = targetIndex + 1;
        return (pending[targetIndex], nextIndex < pending.Count ? pending[nextIndex] : null);
    }
}

/// <summary>リテラル／正規表現検索で、文字列のどこを強調するかを求める。</summary>
public static class SearchTextMatcher
{
    public static IReadOnlyList<SearchTextMatch> FindMatches(
        string text, string query, bool useRegex, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
            return Array.Empty<SearchTextMatch>();

        if (!useRegex)
        {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var matches = new List<SearchTextMatch>();
            var index = 0;
            while (index < text.Length)
            {
                var hit = text.IndexOf(query, index, comparison);
                if (hit < 0) break;
                matches.Add(new SearchTextMatch(hit, query.Length));
                index = hit + query.Length;
            }
            return matches;
        }

        Regex regex;
        try
        {
            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            regex = new Regex(query, options, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException) { return Array.Empty<SearchTextMatch>(); }

        var results = new List<SearchTextMatch>();
        try
        {
            foreach (Match match in regex.Matches(text))
                if (match.Length > 0)
                    results.Add(new SearchTextMatch(match.Index, match.Length));
        }
        catch (RegexMatchTimeoutException)
        {
            // 取得できた範囲だけを返す。描画側は残りを通常文字として表示する。
        }
        return results;
    }
}

/// <summary>検索ルート入力に対するワークスペースフォルダー候補を作る。</summary>
public static class WorkspaceFolderSuggestions
{
    public const int MaxResults = 20;

    internal static bool ShouldShow(IReadOnlyList<string> matches, string? input)
    {
        if (matches.Count == 0)
            return false;
        var text = (input ?? "").Replace('\\', '/').TrimEnd('/');
        return matches.Count != 1
            || !string.Equals(matches[0], text, StringComparison.OrdinalIgnoreCase);
    }

    internal static int MoveSelectionIndex(int count, int currentIndex, int delta)
        => count <= 0 ? -1 : Math.Clamp(currentIndex + delta, 0, count - 1);

    internal static RootSuggestionKeyAction ResolveKeyAction(
        Key key, ModifierKeys modifiers, bool popupOpen, bool hasSelectedItem)
    {
        if (popupOpen)
        {
            if (key == Key.Down) return RootSuggestionKeyAction.MoveNext;
            if (key == Key.Up) return RootSuggestionKeyAction.MovePrevious;
            if (key == Key.Escape) return RootSuggestionKeyAction.Dismiss;
            if ((key is Key.Enter or Key.Tab) && hasSelectedItem)
                return RootSuggestionKeyAction.Accept;
        }

        if (key == Key.Space && modifiers == ModifierKeys.Control)
            return RootSuggestionKeyAction.Show;
        return key == Key.Enter ? RootSuggestionKeyAction.Commit : RootSuggestionKeyAction.None;
    }

    public static List<string> Compute(IReadOnlyList<string>? folders, string? input)
    {
        var empty = new List<string>();
        if (folders is null || folders.Count == 0)
            return empty;

        var text = (input ?? "").Replace('\\', '/');
        var lastSep = text.LastIndexOf('/');
        var dirPart = lastSep >= 0 ? text[..lastSep] : "";
        var prefix = lastSep >= 0 ? text[(lastSep + 1)..] : text;

        if (folders.Count == 1)
            return SuggestSubfolders(folders[0], dirPart, prefix);

        if (string.IsNullOrEmpty(dirPart))
            return folders.Select(LabelFor)
                .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxResults)
                .ToList();

        var nameSep = dirPart.IndexOf('/');
        var rootName = nameSep >= 0 ? dirPart[..nameSep] : dirPart;
        var restDir = nameSep >= 0 ? dirPart[(nameSep + 1)..] : "";
        var folder = folders.FirstOrDefault(path =>
            string.Equals(LabelFor(path), rootName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
            return empty;

        return SuggestSubfolders(folder, restDir, prefix)
            .Select(path => rootName + "/" + path)
            .ToList();
    }

    private static List<string> SuggestSubfolders(string root, string dirPart, string prefix)
    {
        var empty = new List<string>();
        string baseDir;
        try { baseDir = string.IsNullOrEmpty(dirPart) ? root : Path.GetFullPath(dirPart, root); }
        catch { return empty; }
        if (!Directory.Exists(baseDir))
            return empty;

        try
        {
            return Directory.EnumerateDirectories(baseDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name)
                    && !name!.StartsWith('.')
                    && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxResults)
                .Select(name => string.IsNullOrEmpty(dirPart) ? name! : dirPart + "/" + name)
                .ToList();
        }
        catch { return empty; }
    }

    private static string LabelFor(string fullPath)
    {
        var name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? fullPath : name;
    }
}

internal enum RootSuggestionKeyAction
{
    None,
    MoveNext,
    MovePrevious,
    Dismiss,
    Accept,
    Show,
    Commit,
}

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

    public async Task<SearchPanelResult> AdvancedAsync(AdvancedSearchOptions options, string? searchRoot,
        CancellationToken cancellationToken)
    {
        var hits = await _search.SearchFilesAsync(options, cancellationToken, searchRoot);
        var groups = await Task.Run(() => hits.Select(hit => new SearchFileGroup(hit)).ToList(), cancellationToken);
        var roots = await Task.Run(() => _mapper.Map(groups), cancellationToken);
        var matchCount = groups.Sum(group => group.Count);
        var status = groups.Count == 0
            ? "一致なし"
            : options.ContentQuery is { Length: > 0 }
                ? $"{groups.Count} ファイル / {matchCount} 件"
                : $"{groups.Count} ファイル";
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

    /// <summary>1件（<paramref name="line"/>・<paramref name="column"/>で指定した一致、ともに1始まり）だけを
    /// 置換して書き戻す。ディスク上の現在の内容を読み直してから、その行の中でその列にある一致を探して
    /// 置換する（検索実行後に内容がずれて見つからなければ何もせず false を返す＝安全側）。
    /// 不正な正規表現（<paramref name="useRegex"/> 時）も false。</summary>
    public bool ReplaceOneInFile(string fullPath, string query, string replacement, bool caseSensitive, bool useRegex,
        int line, int column)
    {
        string text;
        try { text = File.ReadAllText(fullPath); }
        catch { return false; }

        var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        var pattern = useRegex ? query : Regex.Escape(query);
        Regex regex;
        try { regex = new Regex(pattern, options); }
        catch { return false; }

        var lineInfo = FindLine(text, line);
        if (lineInfo is not { } found) return false;

        // 一致行の検索は grep 実行時と同じく行単位（ContentSearchHit.Column も行内オフセットのため）。
        var match = regex.Matches(found.Text).FirstOrDefault(m => m.Index + 1 == column);
        if (match is null) return false;

        var absoluteIndex = found.Start + match.Index;
        var replacementText = useRegex ? match.Result(replacement) : replacement;
        var result = text.Remove(absoluteIndex, match.Length).Insert(absoluteIndex, replacementText);
        File.WriteAllText(fullPath, result);
        return true;
    }

    /// <summary>1始まりの行番号から、その行の開始オフセット（行末の改行文字は含まない範囲のテキスト）を返す。
    /// 行が存在しなければ null。</summary>
    private static (int Start, string Text)? FindLine(string text, int lineNumber)
    {
        var start = 0;
        var current = 1;
        while (true)
        {
            var newlineIndex = text.IndexOf('\n', start);
            var end = newlineIndex < 0 ? text.Length : newlineIndex;
            var trimmedEnd = end > start && text[end - 1] == '\r' ? end - 1 : end;
            if (current == lineNumber)
                return (start, text[start..trimmedEnd]);
            if (newlineIndex < 0)
                return null;
            start = newlineIndex + 1;
            current++;
        }
    }
}
