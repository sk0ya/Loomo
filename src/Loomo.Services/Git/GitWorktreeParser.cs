using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>
/// <c>git worktree list --porcelain</c> の出力を読む。1件は空行区切りのブロックで、
/// <c>worktree &lt;path&gt;</c> で始まり、<c>HEAD &lt;hash&gt;</c> / <c>branch refs/heads/x</c> / <c>detached</c> /
/// <c>bare</c> / <c>locked [理由]</c> / <c>prunable [理由]</c> が続く。先頭のブロックがメインの作業ツリー。
/// 知らない行は読み飛ばす（git が行を足しても壊れない）。
/// </summary>
public static class GitWorktreeParser
{
    public static IReadOnlyList<GitWorktreeInfo> Parse(string? output)
    {
        var result = new List<GitWorktreeInfo>();
        if (string.IsNullOrEmpty(output)) return result;

        GitWorktreeInfo? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var (key, value) = Split(line);
            if (key == "worktree")
            {
                Flush();
                if (value.Length == 0) continue;
                current = new GitWorktreeInfo
                {
                    Path = GitWorktreeInfo.Normalize(value.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                    IsMain = result.Count == 0,
                };
                continue;
            }
            if (current is null) continue;

            current = key switch
            {
                "HEAD" => current with { Head = value },
                "branch" => current with { Branch = ShortBranch(value) },
                "detached" => current with { IsDetached = true },
                "bare" => current with { IsBare = true },
                "locked" => current with { IsLocked = true, LockReason = value.Length > 0 ? value : null },
                "prunable" => current with { IsPrunable = true, PrunableReason = value.Length > 0 ? value : null },
                _ => current,
            };
        }
        Flush();
        return result;

        void Flush()
        {
            if (current is not null) result.Add(current);
            current = null;
        }
    }

    /// <summary><c>refs/heads/feature/x</c> → <c>feature/x</c>。それ以外の形はそのまま。</summary>
    public static string ShortBranch(string reference)
    {
        const string prefix = "refs/heads/";
        return reference.StartsWith(prefix, StringComparison.Ordinal) ? reference[prefix.Length..] : reference;
    }

    private static (string Key, string Value) Split(string line)
    {
        var space = line.IndexOf(' ');
        return space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
    }
}
