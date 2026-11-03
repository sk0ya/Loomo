using System;
using System.IO;

namespace sk0ya.Loomo.Services;

/// <summary>ファイルが属するワークツリーと、そこでチェックアウトしているブランチ（切り離した HEAD なら null）。</summary>
public sealed record GitWorkTreeHead(string Root, string? Branch);

/// <summary>
/// ファイル → それを含むワークツリーの根とブランチ。git を起こさず <c>.git</c> と <c>HEAD</c> を読むだけ（ファイルを開くたびに
/// 問うので、プロセスを立てない）。ワークツリー（<c>git worktree add</c>）は <c>.git</c> がファイルで、<c>gitdir: …</c> の先に
/// そのワークツリーの HEAD がある——本体のクローンと同じリポジトリでも、<b>ワークツリーごとにブランチが違う</b>のを正しく読む。
/// </summary>
public static class GitWorkTreeHeads
{
    public static GitWorkTreeHead? Find(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        try
        {
            for (var dir = Path.GetDirectoryName(Path.GetFullPath(filePath)); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                var dotGit = Path.Combine(dir, ".git");
                string? gitDir = null;
                if (Directory.Exists(dotGit))
                    gitDir = dotGit;
                else if (File.Exists(dotGit))
                    gitDir = ReadGitDirPointer(dotGit, dir);
                else
                    continue;
                if (gitDir is null)
                    return new GitWorkTreeHead(dir, null);
                var headFile = Path.Combine(gitDir, "HEAD");
                return new GitWorkTreeHead(dir, File.Exists(headFile) ? BranchFromHead(File.ReadAllText(headFile)) : null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    /// <summary><c>ref: refs/heads/feature/x</c> → <c>feature/x</c>。切り離した HEAD（ハッシュ）は null。</summary>
    public static string? BranchFromHead(string head)
    {
        var text = head.Trim();
        const string prefix = "ref: refs/heads/";
        return text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length ? text[prefix.Length..] : null;
    }

    /// <summary>ワークツリーの <c>.git</c> ファイル（<c>gitdir: &lt;パス&gt;</c>。相対なら <c>.git</c> の場所から）。</summary>
    private static string? ReadGitDirPointer(string dotGitFile, string workTree)
    {
        var line = File.ReadAllText(dotGitFile).Trim();
        const string prefix = "gitdir:";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var path = line[prefix.Length..].Trim();
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(workTree, path));
    }
}
