using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 検索ペインのタブに残した検索結果（VS Code の Search Editor 相当・設計書 §23.3.1）。
/// 残した瞬間の結果（パス・行・行テキスト）をそのまま持つスナップショットで、新しい検索をしても、
/// ファイルがその後変わっても書き換えない——「さっきの結果」と見比べるための面だから。
/// 結果ツリー（<see cref="GetRoots"/>）は初めて見せるときに組む（復元直後の全タブぶんを起動時に作らない）。
/// </summary>
public sealed class SearchResultTab
{
    /// <summary>1枚に残す件数の上限。workspaces.json は保存のたびに書き出すので、際限なく膨らませない
    /// （grep 自体が 1000 件で打ち切るので、ふだんの検索はまるごと収まる）。</summary>
    public const int MaxHits = 5000;

    private readonly List<SearchTabHitSnapshot> _hits;
    private IReadOnlyList<object>? _roots;

    private SearchResultTab(Guid id, SearchScope scope, string query, string nameQuery,
        bool caseSensitive, bool useRegex, DateTime createdUtc, bool truncated, List<SearchTabHitSnapshot> hits)
    {
        Id = id;
        Scope = scope;
        Query = query;
        NameQuery = nameQuery;
        CaseSensitive = caseSensitive;
        UseRegex = useRegex;
        CreatedUtc = createdUtc;
        Truncated = truncated;
        _hits = hits;
    }

    public Guid Id { get; }
    public SearchScope Scope { get; }

    /// <summary>一致行で強調する語（見出しにも使う）。</summary>
    public string Query { get; }

    /// <summary>ファイル名で強調する語。</summary>
    public string NameQuery { get; }

    public bool CaseSensitive { get; }
    public bool UseRegex { get; }
    public DateTime CreatedUtc { get; }
    public bool Truncated { get; }

    /// <summary>残した結果（読み取り専用。保存のたびにこの同じ一覧を書き出すので、作った後は決して変えない）。</summary>
    public IReadOnlyList<SearchTabHitSnapshot> Hits => _hits;

    public int MatchCount => _hits.Count(h => h.Line > 0);
    public int FileCount => _hits.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>タブの見出し（TABS・ペインのヘッダー・▾ 一覧に出す）。</summary>
    public string Title => $"{ScopeLabel(Scope)}「{Shorten(Label, 40)}」";

    /// <summary>件数（「12 件 / 4 ファイル」。行を持たない結果はファイル数だけ）。</summary>
    public string CountText
    {
        get
        {
            var count = MatchCount > 0 ? $"{MatchCount} 件 / {FileCount} ファイル" : $"{FileCount} ファイル";
            return Truncated ? count + "（上限で打ち切り）" : count;
        }
    }

    /// <summary>件数と残した時刻（タブを見ているときの帯に出す）。</summary>
    public string Summary => $"{CountText} · {CreatedUtc.ToLocalTime():MM-dd HH:mm} に残した結果";

    /// <summary>結果ハイライトを正規表現として扱うか（テキスト grep と詳細検索の内容条件だけ）。</summary>
    public bool HighlightUseRegex => Scope is SearchScope.Text or SearchScope.Advanced && UseRegex;

    /// <summary>結果ハイライトで大小を区別するか。</summary>
    public bool HighlightCaseSensitive => Scope is SearchScope.Text or SearchScope.Advanced && CaseSensitive;

    /// <summary>エディタで塗る語（Editor の HighlightSearch はリテラル限定なので正規表現では空）。</summary>
    public string EditorHighlightTerm => Scope is SearchScope.Text or SearchScope.Advanced && !UseRegex ? Query : "";

    /// <summary>EditorSupport（プレビュー等）で塗る語。</summary>
    public string SupportHighlightTerm => Scope is SearchScope.Text or SearchScope.Advanced ? Query : "";

    private string Label
        => !string.IsNullOrWhiteSpace(Query) ? Query
            : !string.IsNullOrWhiteSpace(NameQuery) ? NameQuery
            : "詳細検索";

    /// <summary>いま見ている検索結果ツリーからタブを作る。ターミナル内の一致はその場限りの実体
    /// （次に開いたターミナルでは同じ行を指せない）なので入れない。何も残らなければ null。</summary>
    public static SearchResultTab? Capture(SearchScope scope, string query, string nameQuery,
        bool caseSensitive, bool useRegex, IEnumerable<SearchFileGroup> groups, DateTime? nowUtc = null)
    {
        var hits = new List<SearchTabHitSnapshot>();
        var truncated = false;
        foreach (var group in groups)
        {
            if (string.IsNullOrEmpty(group.FullPath))
                continue;
            if (group.Matches.Count == 0)
            {
                if (!TryAdd(hits, new SearchTabHitSnapshot { Path = group.FullPath })) { truncated = true; break; }
                continue;
            }
            foreach (var match in group.Matches)
            {
                if (match.IsTerminal) continue;
                if (!TryAdd(hits, new SearchTabHitSnapshot
                    {
                        Path = match.FullPath,
                        Line = Math.Max(1, match.Line),
                        Column = Math.Max(1, match.Column),
                        Text = match.LineText,
                    }))
                {
                    truncated = true;
                    break;
                }
            }
            if (truncated) break;
        }
        if (hits.Count == 0)
            return null;
        return new SearchResultTab(Guid.NewGuid(), scope, query ?? "", nameQuery ?? "",
            caseSensitive, useRegex, nowUtc ?? DateTime.UtcNow, truncated, hits);
    }

    private static bool TryAdd(List<SearchTabHitSnapshot> hits, SearchTabHitSnapshot hit)
    {
        if (hits.Count >= MaxHits) return false;
        hits.Add(hit);
        return true;
    }

    /// <summary>保存形から戻す。結果の無い・読めない保存は捨てる（null）。</summary>
    public static SearchResultTab? FromSnapshot(SearchTabSnapshot? snapshot)
    {
        if (snapshot is null)
            return null;
        var hits = (snapshot.Hits ?? new())
            .Where(h => h is not null && !string.IsNullOrWhiteSpace(h.Path))
            .Take(MaxHits)
            .ToList();
        if (hits.Count == 0)
            return null;
        var scope = Enum.TryParse<SearchScope>(snapshot.Scope, out var parsed) ? parsed : SearchScope.Text;
        return new SearchResultTab(snapshot.Id == Guid.Empty ? Guid.NewGuid() : snapshot.Id, scope,
            snapshot.Query ?? "", snapshot.NameQuery ?? snapshot.Query ?? "",
            snapshot.CaseSensitive, snapshot.UseRegex, snapshot.CreatedUtc, snapshot.Truncated, hits);
    }

    /// <summary>保存形へ。結果の一覧は同じものを渡す（作った後は変えないので、書き出しスレッドに渡しても安全）。</summary>
    public SearchTabSnapshot ToSnapshot(bool isActive) => new()
    {
        Id = Id,
        Scope = Scope.ToString(),
        Query = Query,
        NameQuery = NameQuery,
        CaseSensitive = CaseSensitive,
        UseRegex = UseRegex,
        CreatedUtc = CreatedUtc,
        IsActive = isActive,
        Truncated = Truncated,
        Hits = _hits,
    };

    /// <summary>このタブの結果ツリー。表示パス（マルチルートならフォルダー名付き）はその時点の
    /// ワークスペースで組み直す——保存したのはフルパスだけなので、フォルダーを足しても見出しがずれない。</summary>
    public IReadOnlyList<object> GetRoots(IWorkspaceService workspace, SearchResultTreeMapper mapper)
        => _roots ??= mapper.Map(BuildGroups(workspace));

    /// <summary>ワークスペースのフォルダー構成が変わったので、次に見せるとき表示パスを組み直す。</summary>
    public void InvalidateRoots() => _roots = null;

    private List<SearchFileGroup> BuildGroups(IWorkspaceService workspace)
        => _hits
            .GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var path = g.First().Path;
                var relative = DisplayPath(workspace, path);
                var matches = g.Where(h => h.Line > 0)
                    .Select(h => SearchMatchItem.ForSnapshot(path, relative, h.Line, h.Column, h.Text ?? ""));
                return new SearchFileGroup(path, relative, matches);
            })
            .ToList();

    private static string DisplayPath(IWorkspaceService workspace, string path)
        => workspace.ToDisplayPath(path).Replace('\\', '/');

    internal static string ScopeLabel(SearchScope scope) => scope switch
    {
        SearchScope.FileName => "ファイル",
        SearchScope.Advanced => "詳細",
        SearchScope.Terminal => "ターミナル",
        SearchScope.Class => "クラス",
        SearchScope.Symbol => "シンボル",
        _ => "テキスト",
    };

    private static string Shorten(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}

/// <summary>検索ペイン内のタブ帯の1枚。<see cref="TabId"/> が null なら「現在の検索」（閉じられない）。</summary>
public sealed partial class SearchTabStripEntry : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public SearchTabStripEntry(Guid? tabId, string title, string? toolTip, bool isActive)
    {
        TabId = tabId;
        Title = title;
        ToolTip = toolTip;
        _isActive = isActive;
    }

    public Guid? TabId { get; }
    public string Title { get; }
    public string? ToolTip { get; }
    public bool CanClose => TabId is not null;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isActive;
}
