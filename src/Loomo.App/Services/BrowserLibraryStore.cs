namespace sk0ya.Loomo.App.Services;

/// <summary>ブックマークの1件。</summary>
public sealed class BrowserBookmark
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>訪問履歴の1件（URL 単位でまとめ、訪問のたびに <see cref="VisitCount"/> と
/// <see cref="LastVisitedUtc"/> を更新する）。</summary>
public sealed class BrowserHistoryEntry
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public DateTime LastVisitedUtc { get; set; } = DateTime.UtcNow;
    public int VisitCount { get; set; } = 1;
}

/// <summary>browser.json の中身（このファイルの形が永続化の正本）。</summary>
public sealed class BrowserLibrarySnapshot
{
    public List<BrowserBookmark> Bookmarks { get; set; } = new();
    public List<BrowserHistoryEntry> History { get; set; } = new();
}

/// <summary>
/// ブラウザペインのブックマークと訪問履歴を <c>%APPDATA%/Loomo/browser.json</c> に永続化する。
///
/// <para><b>ワークスペース単位ではなくアプリ単位</b>に持つ。ブックマークと履歴は「どのプロジェクトを
/// 開いていたか」ではなく「その人が何を見ていたか」に属する資産で、ワークスペースを切り替えたら
/// 消えるのでは道具として使えない（ワークスペース固有のタブ構成は
/// <see cref="WorkspaceStateStore"/> 側の <c>BrowserTabs</c> が持つ）。</para>
///
/// <para>人間のナビゲーションを記録するという意味では軌跡（設計書 §27）と重なるが、あちらは
/// 「いつどこへ行ったか」を時系列で辿るための点列で、こちらは「よく行く場所」を頻度つきで
/// 引くための索引——アドレス欄の候補補完という別の用途を持つので別ファイルにしている。</para>
/// </summary>
public sealed class BrowserLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    /// <summary>履歴の保持上限（超えたら最終訪問が古いものから捨てる）。
    /// ページを開くたびにファイル全体を書き直すので、際限なく増やさない——候補の質に効くのは
    /// せいぜい直近数百件で、その先は書き込みが重くなるだけ。</summary>
    public int MaxHistory { get; }

    public BrowserLibraryStore() : this(DefaultPath()) { }

    public BrowserLibraryStore(string filePath, int maxHistory = 1000)
    {
        _filePath = filePath;
        MaxHistory = maxHistory;
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "browser.json");

    public BrowserLibrarySnapshot Load()
    {
        if (!File.Exists(_filePath))
            return new BrowserLibrarySnapshot();
        try
        {
            return JsonSerializer.Deserialize<BrowserLibrarySnapshot>(
                File.ReadAllText(_filePath), JsonOptions) ?? new BrowserLibrarySnapshot();
        }
        catch
        {
            // 壊れたファイルで起動を止めない（次の保存で書き直る）。
            return new BrowserLibrarySnapshot();
        }
    }

    public void Save(BrowserLibrarySnapshot snapshot)
    {
        // 上限は渡された snapshot 自体に反映する（保存用のコピーだけ切り詰めると、呼び出し側が
        // 持ち続けている実体は際限なく伸び、候補検索も履歴一覧もその全長を毎回歩くことになる）。
        snapshot.History = BrowserLibrary.Trim(snapshot.History, MaxHistory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch
        {
            // 保存できなくてもブラウズは続けられる（次の保存で復帰する）。
        }
    }
}

/// <summary>アドレス欄の候補（履歴・ブックマークのどちらから来たかを持つ）。</summary>
public sealed record BrowserSuggestion(string Url, string? Title, bool IsBookmark)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title!;
    public string Glyph => IsBookmark ? "★" : "🕘";
}

/// <summary>ブックマーク・履歴に対する判断をまとめた純関数群（UI も WebView2 も触らないのでテストできる）。
/// <see cref="BrowserLibraryStore"/> が入出力、こちらが規則という分担。</summary>
public static class BrowserLibrary
{
    /// <summary>履歴に残さない URL（既定ページ・about: 等の「行き先」と言えないもの）。
    /// 軌跡側の <see cref="TrailLogic.IsRecordableBrowserUrl"/> と同じ考え方。</summary>
    public static bool IsRecordable(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>同じページかどうかの判定に使う正規化（末尾スラッシュとフラグメントの差を無視する）。
    /// クエリは残す——検索結果や GitHub の絞り込みは別ページなので。</summary>
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";
        var text = url.Trim();
        var hash = text.IndexOf('#');
        if (hash >= 0)
            text = text[..hash];
        return text.Length > 1 && text.EndsWith('/') && !text.EndsWith("//", StringComparison.Ordinal)
            ? text[..^1]
            : text;
    }

    public static bool SameUrl(string? a, string? b)
        => string.Equals(Normalize(a ?? ""), Normalize(b ?? ""), StringComparison.OrdinalIgnoreCase);

    /// <summary>訪問を1件たたむ。同じ URL が既にあれば件数を増やして先頭へ、無ければ追加する。
    /// 返すリストは「最終訪問が新しい順」。</summary>
    public static List<BrowserHistoryEntry> RecordVisit(
        IEnumerable<BrowserHistoryEntry> history, string url, string? title, DateTime nowUtc)
    {
        var list = history.ToList();
        var existing = list.FirstOrDefault(e => SameUrl(e.Url, url));
        if (existing is not null)
        {
            existing.VisitCount++;
            existing.LastVisitedUtc = nowUtc;
            // タイトルは後から確定することがある（ナビゲーション完了時点では空のことがある）ので、
            // 空でないものが来たときだけ上書きする。
            if (!string.IsNullOrWhiteSpace(title))
                existing.Title = title;
            list.Remove(existing);
            list.Insert(0, existing);
            return list;
        }
        list.Insert(0, new BrowserHistoryEntry
        {
            Url = url,
            Title = title,
            LastVisitedUtc = nowUtc,
            VisitCount = 1,
        });
        return list;
    }

    /// <summary>上限を超えたぶんを最終訪問の古い順に捨てる。</summary>
    public static List<BrowserHistoryEntry> Trim(IEnumerable<BrowserHistoryEntry> history, int max)
    {
        var list = history.ToList();
        return list.Count <= max
            ? list
            : list.OrderByDescending(e => e.LastVisitedUtc).Take(max).ToList();
    }

    /// <summary>アドレス欄に打った文字から候補を選ぶ。ブックマークを履歴より上に、
    /// 同じ出自なら「URL の先頭一致 → 訪問回数 → 新しさ」の順で並べる。
    /// 打った文字が空なら候補は出さない（何も打っていないのに一覧が降りてくるのは邪魔）。</summary>
    public static List<BrowserSuggestion> Suggest(
        IEnumerable<BrowserBookmark> bookmarks,
        IEnumerable<BrowserHistoryEntry> history,
        string? query,
        int limit = 8)
    {
        var text = query?.Trim() ?? "";
        if (text.Length == 0)
            return new List<BrowserSuggestion>();

        var results = new List<(BrowserSuggestion Suggestion, int Rank, int Visits, DateTime When)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bookmark in bookmarks)
        {
            if (Score(bookmark.Url, bookmark.Title, text) is not { } score)
                continue;
            if (!seen.Add(Normalize(bookmark.Url)))
                continue;
            results.Add((new BrowserSuggestion(bookmark.Url, bookmark.Title, IsBookmark: true),
                score, int.MaxValue, bookmark.AddedUtc));
        }
        foreach (var entry in history)
        {
            if (Score(entry.Url, entry.Title, text) is not { } score)
                continue;
            if (!seen.Add(Normalize(entry.Url)))
                continue;
            results.Add((new BrowserSuggestion(entry.Url, entry.Title, IsBookmark: false),
                score, entry.VisitCount, entry.LastVisitedUtc));
        }

        return results
            .OrderBy(r => r.Suggestion.IsBookmark ? 0 : 1)
            .ThenBy(r => r.Rank)
            .ThenByDescending(r => r.Visits)
            .ThenByDescending(r => r.When)
            .Take(limit)
            .Select(r => r.Suggestion)
            .ToList();
    }

    /// <summary>一致の強さ（小さいほど良い）。一致しなければ null。
    /// スキーム・www を飛ばした先頭一致を最上位に置く——「git」と打って
    /// <c>https://github.com/…</c> が出ないと候補として使い物にならない。</summary>
    private static int? Score(string url, string? title, string query)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var host = StripScheme(url);
        if (host.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (url.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (url.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (!string.IsNullOrWhiteSpace(title) && title!.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        return null;
    }

    private static string StripScheme(string url)
    {
        var text = url;
        foreach (var scheme in new[] { "https://", "http://" })
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                text = text[scheme.Length..];
                break;
            }
        return text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? text[4..] : text;
    }
}
