using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.Services.GitHub;

/// <summary>github.com の Web ページの種類（ブラウザサポート §41）。</summary>
public enum GitHubWebPageKind
{
    /// <summary>リポジトリの中だが、下の種類のどれでもない。</summary>
    Repository,
    /// <summary>PR（<c>/owner/repo/pull/12</c>・<c>/files</c> 等の副画面も）。</summary>
    PullRequest,
    /// <summary>Issue（<c>/owner/repo/issues/34</c>）。</summary>
    Issue,
    /// <summary>コミット（<c>/owner/repo/commit/&lt;sha&gt;</c>・PR の中の <c>/pull/12/commits/&lt;sha&gt;</c>）。</summary>
    Commit,
    /// <summary>ファイル（<c>/owner/repo/blob/&lt;ref&gt;/&lt;path&gt;#L12</c>）。</summary>
    File,
    /// <summary>GitHub Actions の実行（<c>/owner/repo/actions/runs/123</c>）。</summary>
    ActionsRun,
}

/// <summary>
/// github.com のページが指しているもの。<paramref name="BlobPath"/> は <c>blob/</c> の後ろ全部（ref とパスの境目は
/// URL からは決まらない——ブランチ名に / を含められるので、手元のクローンにあるファイルで決める。<see cref="GitHubWebUrl.LocalPath"/>）。
/// </summary>
public sealed record GitHubWebPage(
    GitHubWebPageKind Kind,
    string Owner,
    string Repository,
    int Number = 0,
    string CommitSha = "",
    string BlobPath = "",
    int Line = 0,
    long RunId = 0)
{
    /// <summary><c>owner/repo</c>（gh の <c>--repo</c> に渡す形）。</summary>
    public string Slug => $"{Owner}/{Repository}";

    /// <summary>リモートの URL を <see cref="GitHostingUrl.TryGetGitHubRepositoryUrl"/> で直した形と比べる URL。</summary>
    public string RepositoryUrl => $"https://github.com/{Owner}/{Repository}";

    /// <summary>ファイルの名前（見出し用。ref とパスの境目が分からなくても末尾の段は名前）。</summary>
    public string FileName => BlobPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
}

/// <summary>ブラウザで開いている URL が github.com のどのページかを読む（純粋）。ページの中身は読まない（§41.2）。</summary>
public static class GitHubWebUrl
{
    /// <summary>リポジトリの名前に見える段のうち、GitHub 自身のページ（設定・通知・検索…）のもの。</summary>
    private static readonly HashSet<string> ReservedOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "settings", "notifications", "search", "marketplace", "explore", "topics", "trending", "orgs", "users",
        "login", "logout", "sponsors", "features", "pricing", "about", "apps", "codespaces", "pulls", "issues", "new",
    };

    public static GitHubWebPage? TryParse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
            return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToList();
        if (segments.Count < 2 || ReservedOwners.Contains(segments[0]))
            return null;
        var owner = segments[0];
        var repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        var rest = segments.Skip(2).ToList();
        string At(int i) => i < rest.Count ? rest[i] : "";

        switch (At(0).ToLowerInvariant())
        {
            case "pull" when int.TryParse(At(1), out var pr) && pr > 0:
                return At(2).Equals("commits", StringComparison.OrdinalIgnoreCase) && IsHash(At(3))
                    ? new GitHubWebPage(GitHubWebPageKind.Commit, owner, repository, Number: pr, CommitSha: At(3))
                    : new GitHubWebPage(GitHubWebPageKind.PullRequest, owner, repository, Number: pr);
            case "issues" when int.TryParse(At(1), out var issue) && issue > 0:
                return new GitHubWebPage(GitHubWebPageKind.Issue, owner, repository, Number: issue);
            case "commit" when IsHash(At(1)):
                return new GitHubWebPage(GitHubWebPageKind.Commit, owner, repository, CommitSha: At(1));
            case "blob" when rest.Count >= 3:
                return new GitHubWebPage(GitHubWebPageKind.File, owner, repository,
                    BlobPath: string.Join('/', rest.Skip(1)), Line: LineFromFragment(uri.Fragment));
            case "actions" when At(1).Equals("runs", StringComparison.OrdinalIgnoreCase) && long.TryParse(At(2), out var run) && run > 0:
                return new GitHubWebPage(GitHubWebPageKind.ActionsRun, owner, repository, RunId: run);
            default:
                return new GitHubWebPage(GitHubWebPageKind.Repository, owner, repository);
        }
    }

    /// <summary>
    /// <c>blob/</c> の後ろ（ref ＋ パス）を手元のクローンのファイルへ。ref は 1 段とは限らない（<c>feature/x</c>）ので、
    /// 先頭から 1 段ずつ ref の側へ寄せながら、クローンの下にあるファイルを探す。<c>..</c> を含むものは開かない。
    /// </summary>
    public static string? LocalPath(string repositoryRoot, string blobPath, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var segments = blobPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments.Any(s => s is ".." or "."))
            return null;
        var root = Path.GetFullPath(repositoryRoot);
        for (var refLength = 1; refLength < segments.Length; refLength++)
        {
            var candidate = Path.Combine([root, .. segments.Skip(refLength)]);
            if (exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary><c>#L12</c>・<c>#L12-L20</c>・<c>#L12C3</c> の開始行。</summary>
    private static int LineFromFragment(string fragment)
    {
        if (!fragment.StartsWith("#L", StringComparison.Ordinal))
            return 0;
        var digits = new string(fragment.Skip(2).TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var line) && line > 0 ? line : 0;
    }

    private static bool IsHash(string text) => text.Length is >= 7 and <= 64 && text.All(Uri.IsHexDigit);
}
