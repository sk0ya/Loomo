using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>Azure Repos の Web ページの種類（ブラウザサポート §41）。</summary>
public enum AzureDevOpsWebPageKind
{
    /// <summary>リポジトリの中だが、下の種類のどれでもない（トップ・ブランチ一覧・PR 一覧など）。</summary>
    Repository,
    /// <summary>PR（<c>…/_git/repo/pullrequest/123</c>）。</summary>
    PullRequest,
    /// <summary>コミット（<c>…/_git/repo/commit/&lt;hash&gt;</c>）。</summary>
    Commit,
    /// <summary>ファイル（<c>…/_git/repo?path=/src/a.cs&amp;line=12</c>）。</summary>
    File,
    /// <summary>パイプラインの実行結果（<c>…/プロジェクト/_build/results?buildId=123</c>）。リポジトリは空。</summary>
    Build,
}

/// <summary>
/// Azure Repos の Web ページが指しているもの。<paramref name="OrganizationUrl"/> は REST の照会先の根
/// （<c>https://dev.azure.com/org</c>・<c>https://org.visualstudio.com</c>・<c>https://host/tfs/Collection</c>。末尾の / 無し）。
/// <paramref name="Line"/> は 1 始まり（無ければ 0）。<paramref name="Version"/> は URL の <c>version</c>
/// （<c>GBmain</c>＝ブランチ・<c>GC…</c>＝コミット・<c>GT…</c>＝タグ）をそのまま持つ。
/// </summary>
public sealed record AzureDevOpsWebPage(
    AzureDevOpsWebPageKind Kind,
    string OrganizationUrl,
    string Project,
    string Repository,
    int PullRequestId = 0,
    string CommitHash = "",
    string FilePath = "",
    int Line = 0,
    string Version = "",
    int BuildId = 0)
{
    /// <summary><see cref="Version"/> がブランチ（<c>GB…</c>）ならその名前。</summary>
    public string? VersionBranch => Version.StartsWith("GB", StringComparison.Ordinal) && Version.Length > 2 ? Version[2..] : null;
}

/// <summary>
/// ブラウザで開いている URL が Azure Repos のどのページかを読む（純粋）。<b>ページの中身（DOM）は読まない</b>——
/// 組織・プロジェクト・リポジトリ・PR 番号・パス・行はすべて URL に入っているので、ページの作りが変わっても壊れず、
/// ページの中の誰か（第三者の iframe 等）に操られることもない。
/// <para>リモート URL の照合（<see cref="AzureDevOpsPullRequestJson.RemoteMatches"/>）と同じ形を受ける：
/// クラウド（<c>dev.azure.com/組織/…</c>・<c>組織.visualstudio.com/…</c>（<c>DefaultCollection</c> 付きも））と
/// Azure DevOps Server（<c>/tfs/コレクション/…</c>）。プロジェクトを省いた形（<c>組織/_git/repo</c>＝リポジトリ名がプロジェクト名）も。</para>
/// </summary>
public static class AzureDevOpsWebUrl
{
    public static AzureDevOpsWebPage? TryParse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToList();

        // 組織を表す段の数（URL の先頭から）。プロジェクトはその直後、_git の手前に1段。
        var host = uri.Host;
        int organizationSegments;
        if (host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            organizationSegments = 1;
        else if (host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            organizationSegments = segments.Count > 0 && segments[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        else if (segments.FindIndex(s => s.Equals("tfs", StringComparison.OrdinalIgnoreCase)) is var tfs and >= 0)
            organizationSegments = tfs + 2;   // tfs ＋ コレクション
        else
            return null;

        var organizationUrl = uri.GetLeftPart(UriPartial.Authority)
            + string.Concat(uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Take(organizationSegments).Select(s => "/" + s));
        var query = ParseQuery(uri.Query);

        // パイプラインの実行結果（組織/プロジェクト/_build/results?buildId=123）。リポジトリには属さない。
        if (segments.Count >= organizationSegments + 2
            && segments[organizationSegments + 1].Equals("_build", StringComparison.OrdinalIgnoreCase)
            && query.TryGetValue("buildId", out var buildText) && int.TryParse(buildText, out var buildId) && buildId > 0)
            return new AzureDevOpsWebPage(AzureDevOpsWebPageKind.Build, organizationUrl, segments[organizationSegments], "", BuildId: buildId);

        var git = segments.FindIndex(s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
        if (git < 0 || git + 1 >= segments.Count)
            return null;

        string project;
        if (git == organizationSegments + 1)
            project = segments[git - 1];
        else if (git == organizationSegments)
            project = segments[git + 1];   // プロジェクトを省いた形：リポジトリ名＝プロジェクト名
        else
            return null;
        var repository = segments[git + 1];
        var rest = segments.Skip(git + 2).ToList();

        if (rest.Count >= 2 && rest[0].Equals("pullrequest", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(rest[1], out var id) && id > 0)
            return new AzureDevOpsWebPage(AzureDevOpsWebPageKind.PullRequest, organizationUrl, project, repository, PullRequestId: id);

        if (rest.Count >= 2 && rest[0].Equals("commit", StringComparison.OrdinalIgnoreCase) && IsHash(rest[1]))
            return new AzureDevOpsWebPage(AzureDevOpsWebPageKind.Commit, organizationUrl, project, repository, CommitHash: rest[1]);

        // ファイルはリポジトリの直下に ?path=… で来る（_a=contents・_a=history 等の副画面も同じファイル）。
        // path=/ はリポジトリのトップのフォルダーなのでファイルではない。
        if (rest.Count == 0 && query.TryGetValue("path", out var path) && path.Trim('/').Length > 0)
        {
            var line = query.TryGetValue("line", out var lineText) && int.TryParse(lineText, out var l) && l > 0 ? l : 0;
            return new AzureDevOpsWebPage(AzureDevOpsWebPageKind.File, organizationUrl, project, repository,
                FilePath: path, Line: line, Version: query.GetValueOrDefault("version", ""));
        }

        return new AzureDevOpsWebPage(AzureDevOpsWebPageKind.Repository, organizationUrl, project, repository);
    }

    /// <summary>リポジトリの中のパス（<c>/src/a.cs</c>）を、手元のクローンのファイルへ。<c>..</c> でクローンの外へ出るものは null。</summary>
    public static string? LocalPath(string repositoryRoot, string filePath)
    {
        var relative = filePath.Replace('\\', '/').TrimStart('/');
        if (relative.Length == 0)
            return null;
        var root = Path.GetFullPath(repositoryRoot);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static bool IsHash(string text)
        => text.Length is >= 7 and <= 64 && text.All(Uri.IsHexDigit);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }
        return result;
    }
}
