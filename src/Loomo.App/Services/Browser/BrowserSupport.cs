namespace sk0ya.Loomo.App.Services;

/// <summary>ブラウザサポートの道具（§41）。押したときの中身は ShellWindow が持つ（部屋のペインを動かすので）。</summary>
public enum BrowserSupportActionKind
{
    /// <summary>PR の差分を Diff ペインで（分岐点から。§24.27.1 と同じ部品）。</summary>
    PullRequestDiff,
    /// <summary>PR の概要と会話を Markdown の写しでエディタへ（§24.27.1 と同じ）。</summary>
    PullRequestDocument,
    /// <summary>ページのファイルを手元のクローンで開く（その行へ）。</summary>
    OpenLocalFile,
    /// <summary>ページのコミットを Diff ペインで。</summary>
    CommitDiff,
    /// <summary>このリポジトリを Git の対象（リポジトリの帯で選んでいるもの）にする。</summary>
    SelectRepository,
}

/// <summary>道具1つ。<paramref name="NeedsLocalRepository"/> は手元のクローンが要るもの（無ければ出さない）。</summary>
public sealed record BrowserSupportAction(BrowserSupportActionKind Kind, string Label, string Detail, bool NeedsLocalRepository);

/// <summary>前面のタブのページに合った道具の一式。</summary>
public sealed record BrowserSupportPage(
    string Site,
    string Heading,
    string Detail,
    IReadOnlyList<BrowserSupportAction> Actions,
    AzureDevOpsWebPage? AzureDevOps = null);

/// <summary>
/// URL → そのページに合ったブラウザサポート（§41）。EditorSupport が拡張子で、TerminalSupport が前面のプログラムで
/// 見せ方を選ぶのと同じ型で、ここは<b>サイト（URL の形）</b>で道具を選ぶ。拡張機能の <c>matches</c> にあたる。
/// <para>ブラウザの中で完結するもの（整形・翻訳・広告ブロック）は本物の拡張機能が動く（§21.5.2）ので作らない。
/// 載せるのは<b>部屋（エディタ・Diff・Git・ワークスペース）へつなぐ</b>道具だけ。</para>
/// </summary>
public static class BrowserSupportCatalog
{
    public const string AzureDevOpsSite = "Azure DevOps";

    /// <summary>知らないページなら null。</summary>
    public static BrowserSupportPage? Describe(string? url)
        => AzureDevOpsWebUrl.TryParse(url) is { } ado ? DescribeAzureDevOps(ado) : null;

    private static BrowserSupportPage DescribeAzureDevOps(AzureDevOpsWebPage page)
    {
        var where = $"{page.Project} / {page.Repository}";
        var select = new BrowserSupportAction(BrowserSupportActionKind.SelectRepository,
            "このリポジトリを Git の対象にする", "Git パネル・Diff ペインがこのリポジトリを見る", NeedsLocalRepository: true);
        return page.Kind switch
        {
            AzureDevOpsWebPageKind.PullRequest => new BrowserSupportPage(AzureDevOpsSite, $"PR {page.PullRequestId}", where,
            [
                new(BrowserSupportActionKind.PullRequestDiff, "差分を Diff ペインで見る",
                    "入れ先から分かれたあとの変更だけ（Azure Repos の「ファイル」と同じ）。元と入れ先のブランチを手元へ取ってくる", NeedsLocalRepository: true),
                new(BrowserSupportActionKind.PullRequestDocument, "概要と会話をエディタで読む",
                    "説明・レビュアーと投票・スレッドを Markdown の写しに（保存しても Azure DevOps へは戻さない）", NeedsLocalRepository: false),
                select,
            ], page),
            AzureDevOpsWebPageKind.File => new BrowserSupportPage(AzureDevOpsSite, FileHeading(page), Annotate(where, page),
            [
                new(BrowserSupportActionKind.OpenLocalFile, page.Line > 0 ? $"手元のファイルを開く（{page.Line} 行目）" : "手元のファイルを開く",
                    "ワークスペースにあるクローンの同じパス。手元の版はページの版と違うことがある", NeedsLocalRepository: true),
                select,
            ], page),
            AzureDevOpsWebPageKind.Commit => new BrowserSupportPage(AzureDevOpsSite, $"コミット {Short(page.CommitHash)}", where,
            [
                new(BrowserSupportActionKind.CommitDiff, "このコミットを Diff ペインで見る",
                    "手元に無ければリモートから取ってくる", NeedsLocalRepository: true),
                select,
            ], page),
            _ => new BrowserSupportPage(AzureDevOpsSite, page.Repository, where, [select], page),
        };
    }

    private static string FileHeading(AzureDevOpsWebPage page)
    {
        var name = page.FilePath.TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return page.Line > 0 ? $"{name}:{page.Line}" : name;
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
