using System;
using System.IO;

namespace sk0ya.Loomo.Services;

/// <summary>
/// フォルダーが今どのブランチにいるかを、<c>git</c> を起こさずに <c>.git/HEAD</c> を読んで答える（§24.53）。
///
/// <para>TABS のターミナル行はタブの数だけ・コマンドが終わるたびに訊くので、そのたびにプロセスを立てたくない。
/// HEAD は 1 行のファイルで、ワークツリー（§24.17）の <c>.git</c> は <c>gitdir: …</c> を書いたファイルになっている——
/// その 2 つの形だけ読めば足りる。packed-refs や reftable は読まない（ブランチ名は HEAD の 1 行に出ている）。</para>
/// </summary>
public static class GitHeadReader
{
    /// <summary>上へたどる段数の上限（ドライブ直下まで行かずに止める保険）。</summary>
    private const int MaxDepth = 32;

    /// <summary><paramref name="directory"/> を含むリポジトリの HEAD。管理外・読めないときは null。</summary>
    public static GitHeadInfo? Read(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(directory));
            for (var depth = 0; dir is not null && depth < MaxDepth; depth++, dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(dotGit))
                    return ReadHead(dotGit, dir.FullName);
                if (File.Exists(dotGit))
                    return ResolveGitFile(dotGit, dir.FullName) is { } gitDir ? ReadHead(gitDir, dir.FullName) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
        }
        return null;
    }

    /// <summary>HEAD ファイルの中身からブランチ名を取り出す。<c>ref: refs/heads/x</c> なら x、
    /// コミット ID だけならデタッチ（短縮 ID を名前にする）。それ以外は null。</summary>
    public static GitHeadInfo? Parse(string? headText, string workTree)
    {
        var line = headText?.Trim();
        if (string.IsNullOrEmpty(line))
            return null;
        const string prefix = "ref:";
        if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            var refName = line[prefix.Length..].Trim();
            const string heads = "refs/heads/";
            var branch = refName.StartsWith(heads, StringComparison.Ordinal) ? refName[heads.Length..] : refName;
            return branch.Length == 0 ? null : new GitHeadInfo(workTree, branch, IsDetached: false);
        }
        return IsHex(line) && line.Length >= 7
            ? new GitHeadInfo(workTree, line[..7], IsDetached: true)
            : null;
    }

    private static GitHeadInfo? ReadHead(string gitDir, string workTree)
    {
        var head = Path.Combine(gitDir, "HEAD");
        return File.Exists(head) ? Parse(File.ReadAllText(head), workTree) : null;
    }

    /// <summary>ワークツリー・サブモジュールの <c>.git</c> ファイル（<c>gitdir: &lt;path&gt;</c>）が指す場所。</summary>
    private static string? ResolveGitFile(string gitFile, string workTree)
    {
        var text = File.ReadAllText(gitFile).Trim();
        const string prefix = "gitdir:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var path = text[prefix.Length..].Trim();
        if (path.Length == 0)
            return null;
        var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(workTree, path));
        return Directory.Exists(full) ? full : null;
    }

    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }
}

/// <summary>HEAD の読み取り結果。<see cref="WorkTree"/> は <c>.git</c> を持つフォルダー（リポジトリの作業ツリーの根）。</summary>
public sealed record GitHeadInfo(string WorkTree, string Branch, bool IsDetached);
