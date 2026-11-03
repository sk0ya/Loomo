using sk0ya.Loomo.Services.GitHub;

namespace sk0ya.Loomo.App.Services;

/// <summary>ブラウザサポートの道具（§41）。押したときの中身は ShellWindow が持つ（部屋のペインを動かすので）。</summary>
public enum BrowserSupportActionKind
{
    /// <summary>PR の差分を Diff ペインで（分岐点から。§24.27・§24.27.1 と同じ部品）。</summary>
    PullRequestDiff,
    /// <summary>PR の説明・レビュー・会話を、ターミナルの Claude Code のプロンプトへ @参照 で渡す（送信はしない）。</summary>
    PullRequestToClaude,
    /// <summary>Issue の本文と会話を、ターミナルの Claude Code のプロンプトへ @参照 で渡す（送信はしない）。</summary>
    IssueToClaude,
    /// <summary>ページのファイルを手元のクローンで開く（その行へ）。</summary>
    OpenLocalFile,
    /// <summary>ブラウザで開いているローカルの HTML の元ファイルをエディタで開く（§24.45）。</summary>
    OpenSourceFile,
}

/// <summary>道具1つ。<paramref name="NeedsLocalRepository"/> は手元のクローンが要るもの（無ければ出さない）。
/// <paramref name="NeedsClaude"/> はターミナルに Claude Code が居るときだけ出すもの。</summary>
public sealed record BrowserSupportAction(BrowserSupportActionKind Kind, string Label, string Detail, bool NeedsLocalRepository,
    bool NeedsClaude = false);

/// <summary>前に出ているタブのページに合った道具の一式。ページが指すもの（Azure DevOps・GitHub・パッケージ・
/// 手元のサーバー・ローカルの HTML）はどれか1つだけが入る。どれでもない普通のページは <see cref="IsGeneric"/>。</summary>
public sealed record BrowserSupportPage(
    string Site,
    string Heading,
    string Detail,
    IReadOnlyList<BrowserSupportAction> Actions,
    AzureDevOpsWebPage? AzureDevOps = null,
    GitHubWebPage? GitHub = null,
    PackagePage? Package = null,
    int LocalPort = 0,
    string? LocalFilePath = null,
    string? PageAddress = null)
{
    /// <summary>どのサイトにも当たらない普通のページ（記事・ドキュメント）。</summary>
    public bool IsGeneric => AzureDevOps is null && GitHub is null && Package is null && LocalPort == 0 && LocalFilePath is null;

    /// <summary>手元のクローンを探すページか（リポジトリに属するもの）。</summary>
    public bool HasRepository => AzureDevOps is { Repository.Length: > 0 } || GitHub is not null;

    /// <summary>ページのコードブロックの右クリックに「〜へ貼る・送る」を出すか。記事・ドキュメント・パッケージの README のように、
    /// コードを写して使うページだけ。GitHub のファイルや PR のページはページ全体がコードで、手元のファイルを開く方が早い。</summary>
    public bool ShowsCode => IsGeneric || Package is not null;

    /// <summary>同じページかの鍵（道具の並びは毎回作り直した別の列なので、URL から読んだ中身で比べる）。
    /// 普通のページは URL そのもの（フラグメント抜き）で見る——同じホストの別の記事は別のページ。</summary>
    public string Key => $"{Site}|{Heading}|{AzureDevOps}|{GitHub}|{Package}|{LocalPort}|{LocalFilePath}|{PageAddress}";
}

/// <summary>
/// URL → そのページに合ったブラウザサポート（§41）。EditorSupport が拡張子で、TerminalSupport が前面のプログラムで
/// 見せ方を選ぶのと同じ型で、ここは<b>サイト（URL の形）</b>で道具を選ぶ。拡張機能の <c>matches</c> にあたる。
/// <para>ブラウザの中で完結するもの（整形・翻訳・広告ブロック）は本物の拡張機能が動く（§21.5.2）ので作らない。
/// 載せるのは<b>部屋（エディタ・Diff・Git・ワークスペース・ターミナル）へつなぐ</b>道具だけ。</para>
/// </summary>
public static class BrowserSupportCatalog
{
    public const string AzureDevOpsSite = "Azure DevOps";
    public const string GitHubSite = "GitHub";

    /// <summary>http(s) と、Loomo がローカルのファイルを見せている URL のページ。それ以外（about:blank 等）は null。</summary>
    public static BrowserSupportPage? Describe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (LocalFileUrl.TryToPath(url, out var path))
            return new BrowserSupportPage("ローカルのファイル", Path.GetFileName(path), path,
                [new(BrowserSupportActionKind.OpenSourceFile, "元のファイルをエディタで開く", "このページの HTML を書き換える", NeedsLocalRepository: false)],
                LocalFilePath: path);
        if (AzureDevOpsWebUrl.TryParse(url) is { } ado)
            return DescribeAzureDevOps(ado);
        if (GitHubWebUrl.TryParse(url) is { } github)
            return DescribeGitHub(github);
        if (BrowserPackagePages.TryParse(url) is { } package)
            return new BrowserSupportPage(package.EcosystemLabel, package.Name,
                package.Version is { } version ? $"ページの版 {version}" : "版の指定なし（入れると最新）", [], Package: package);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        if (IsLocalHost(uri.Host))
            return new BrowserSupportPage("手元のサーバー", $"{uri.Host}:{uri.Port}", "このポートを開いているプロセスと、それを動かしているターミナル",
                [], LocalPort: uri.Port);
        return new BrowserSupportPage(uri.Host, "", "", [], PageAddress: uri.GetLeftPart(UriPartial.Query));
    }

    private static bool IsLocalHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
           || host is "127.0.0.1" or "[::1]" or "::1" or "0.0.0.0";

    private static BrowserSupportPage DescribeAzureDevOps(AzureDevOpsWebPage page)
    {
        if (page.Kind == AzureDevOpsWebPageKind.Build)
            return new BrowserSupportPage(AzureDevOpsSite, $"パイプラインの実行 {page.BuildId}", page.Project,
                [], page);
        var where = $"{page.Project} / {page.Repository}";
        return page.Kind switch
        {
            AzureDevOpsWebPageKind.PullRequest => new BrowserSupportPage(AzureDevOpsSite, $"PR {page.PullRequestId}", where,
            [
                new(BrowserSupportActionKind.PullRequestDiff, "差分を Diff ペインで見る",
                    "入れ先から分かれたあとの変更だけ（Azure Repos の「ファイル」と同じ）。元と入れ先のブランチを手元へ取ってくる", NeedsLocalRepository: true),
                ToClaude(BrowserSupportActionKind.PullRequestToClaude, "この PR を Claude Code へ渡す", "説明・レビュアーと投票・スレッド"),
            ], page),
            AzureDevOpsWebPageKind.File => new BrowserSupportPage(AzureDevOpsSite, FileHeading(page.FilePath, page.Line), Annotate(where, page),
                [OpenLocalFile(page.Line)], page),
            AzureDevOpsWebPageKind.Commit => new BrowserSupportPage(AzureDevOpsSite, $"コミット {Short(page.CommitHash)}", where, [], page),
            _ => new BrowserSupportPage(AzureDevOpsSite, page.Repository, where, [], page),
        };
    }

    private static BrowserSupportPage DescribeGitHub(GitHubWebPage page)
    {
        return page.Kind switch
        {
            GitHubWebPageKind.PullRequest => new BrowserSupportPage(GitHubSite, $"#{page.Number}", page.Slug,
            [
                new(BrowserSupportActionKind.PullRequestDiff, "差分を Diff ペインで見る",
                    "基準から分かれたあとの変更だけ（GitHub の Files changed と同じ）。PR の先頭（refs/pull/N/head）を手元へ取ってくる", NeedsLocalRepository: true),
                ToClaude(BrowserSupportActionKind.PullRequestToClaude, "この PR を Claude Code へ渡す", "状態・チェック・変わったファイル・会話（gh）"),
            ], GitHub: page),
            GitHubWebPageKind.Issue => new BrowserSupportPage(GitHubSite, $"#{page.Number}", page.Slug,
                [ToClaude(BrowserSupportActionKind.IssueToClaude, "この Issue を Claude Code へ渡す", "本文と会話（gh）")],
                GitHub: page),
            GitHubWebPageKind.File => new BrowserSupportPage(GitHubSite, FileHeading(page.FileName, page.Line), $"{page.Slug}\n{page.BlobPath}",
                [OpenLocalFile(page.Line)], GitHub: page),
            GitHubWebPageKind.Commit => new BrowserSupportPage(GitHubSite, $"コミット {Short(page.CommitSha)}", page.Slug, [], GitHub: page),
            GitHubWebPageKind.ActionsRun => new BrowserSupportPage(GitHubSite, $"Actions の実行 {page.RunId}", page.Slug, [], GitHub: page),
            _ => new BrowserSupportPage(GitHubSite, page.Repository, page.Slug, [], GitHub: page),
        };
    }

    private static BrowserSupportAction OpenLocalFile(int line) => new(BrowserSupportActionKind.OpenLocalFile,
        line > 0 ? $"手元のファイルを開く（{line} 行目）" : "手元のファイルを開く",
        "ワークスペースにあるクローンの同じパス。手元の版はページの版と違うことがある", NeedsLocalRepository: true);

    /// <summary>ページのものを Claude Code へ。ページに書いてあることをエディタへ写し直すのではなく、それを材料に頼む相手へ渡す。</summary>
    private static BrowserSupportAction ToClaude(BrowserSupportActionKind kind, string label, string what) => new(kind, label,
        $"{what}を @参照 でプロンプトへ（送信はしない）", NeedsLocalRepository: false, NeedsClaude: true);

    private static string FileHeading(string path, int line)
    {
        var name = path.TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return line > 0 ? $"{name}:{line}" : name;
    }

    private static string Annotate(string where, AzureDevOpsWebPage page)
    {
        var version = page.VersionBranch is { } branch ? $"ブランチ {branch}"
            : page.Version.StartsWith("GC", StringComparison.Ordinal) && page.Version.Length > 2 ? $"コミット {Short(page.Version[2..])}"
            : page.Version.StartsWith("GT", StringComparison.Ordinal) && page.Version.Length > 2 ? $"タグ {page.Version[2..]}"
            : null;
        return version is null ? $"{where}\n{page.FilePath}" : $"{where}\n{page.FilePath}（{version}）";
    }

    private static string Short(string hash) => hash.Length > 7 ? hash[..7] : hash;
}
