using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>ブランチを片付けの候補にする理由（§24.69）。</summary>
[Flags]
public enum GitBranchCleanupReason
{
    None = 0,
    /// <summary>既定ブランチへ取り込み済み（<c>git branch --merged</c>・自前の作業の跡あり）。</summary>
    Merged = 1,
    /// <summary>上流が消えた——PR がマージされてリモートのブランチが消された、のがいちばん多い（スカッシュマージはこれでしか拾えない）。</summary>
    UpstreamGone = 2,
    /// <summary>先頭のコミットが <see cref="GitBranchCleanup.StaleAfter"/> より前。</summary>
    Stale = 4,
}

/// <summary>
/// ブランチの片付けの判断と見せ方（純ロジック・テスト対象・§24.69）。
/// <para>Tower for Windows 13.0／14.0 の「Fully Merged／Stale」と「なぜ取り込み済みと判断したか」。§24.55 のワークツリーの片付けと同じ形で、
/// ローカルブランチを理由つきで並べ、選んでまとめて消す。</para>
/// <para>消せないもの：いまのブランチ・既定ブランチ・どこかのワークツリーで使っているブランチ（git が断る）・リモートブランチ（ここでは扱わない）。
/// オープンな PR があるブランチは候補にしても最初の印を付けない（作業中のことが多い）。</para>
/// </summary>
public static class GitBranchCleanup
{
    /// <summary>この日数より前から先頭のコミットが動いていなければ候補にする。ワークツリー（14 日）より長い——ブランチは
    /// 置いておくだけなら場所を取らず、しばらく寝かせるのは普通。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>消せない理由。消せるなら null。</summary>
    public static string? BlockedReason(GitBranchInfo branch, string? defaultBranchLocal, IReadOnlySet<string> worktreeBranches)
        => branch.IsRemote ? "リモートブランチ"
            : branch.IsCurrent ? "いまのブランチ"
            : string.Equals(branch.Name, defaultBranchLocal, StringComparison.Ordinal) ? "既定ブランチ"
            : worktreeBranches.Contains(branch.Name) ? "ワークツリーで使用中"
            : null;

    public static GitBranchCleanupReason Reasons(GitBranchInfo branch, bool isMerged, DateTime nowUtc)
    {
        var reasons = GitBranchCleanupReason.None;
        if (isMerged)
            reasons |= GitBranchCleanupReason.Merged;
        if (branch.UpstreamGone)
            reasons |= GitBranchCleanupReason.UpstreamGone;
        if (branch.LastCommit is { } last && nowUtc - last.UtcDateTime > StaleAfter)
            reasons |= GitBranchCleanupReason.Stale;
        return reasons;
    }

    /// <summary>理由の文（行に出す）。なぜそう判断したかを、確かめられる言葉で言う（Tower の Fully Merged Hints）。</summary>
    public static string ReasonText(GitBranchCleanupReason reasons, string? mergedInto, string? upstream)
    {
        var parts = new List<string>(3);
        if (reasons.HasFlag(GitBranchCleanupReason.Merged))
            parts.Add(mergedInto is { Length: > 0 } into ? $"{into} に取り込み済み" : "取り込み済み");
        if (reasons.HasFlag(GitBranchCleanupReason.UpstreamGone))
            parts.Add(upstream is { Length: > 0 } up ? $"上流 {up} が消えた" : "上流が消えた");
        if (reasons.HasFlag(GitBranchCleanupReason.Stale))
            parts.Add($"{StaleAfter.Days}日以上動いていない");
        return string.Join("・", parts);
    }

    /// <summary>行のツールチップ：判断の根拠を 1 つずつ（何を見てそう言っているか）。</summary>
    public static string ReasonDetail(GitBranchCleanupReason reasons, string? mergedInto, string? upstream)
    {
        var lines = new List<string>();
        if (reasons.HasFlag(GitBranchCleanupReason.Merged))
            lines.Add($"取り込み済み: git branch --merged {mergedInto} に載り、reflog に作業の跡がある（作っただけのブランチは数えない）");
        if (reasons.HasFlag(GitBranchCleanupReason.UpstreamGone))
            lines.Add($"上流が消えた: 追跡先 {upstream} がリモートにもう無い（PR のマージでリモートのブランチが消されたときがこれ。スカッシュマージは --merged に載らない）");
        if (reasons.HasFlag(GitBranchCleanupReason.Stale))
            lines.Add($"動いていない: 先頭のコミットが {StaleAfter.Days} 日より前");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>最初に印を付けるか：候補で、消せて、オープンな PR が無いもの。取り込み済みでないもの（上流が消えた・動いていないだけ）は
    /// 消すのに <c>-D</c> が要るので、窓の「取り込まれていないものも消す」を付けるまでは消さない（印は付ける）。</summary>
    public static bool Preselect(GitBranchCleanupReason reasons, bool canClean, bool hasOpenPullRequest)
        => canClean && reasons != GitBranchCleanupReason.None && !hasOpenPullRequest;

    /// <summary><c>origin/main</c> → <c>main</c>（既定ブランチのローカル名）。</summary>
    public static string? LocalName(string? defaultBranch)
        => defaultBranch is { } d && d.IndexOf('/') is var slash and > 0 ? d[(slash + 1)..] : defaultBranch;

    /// <summary>ワークツリーで使っているブランチの集合。</summary>
    public static IReadOnlySet<string> WorktreeBranches(IEnumerable<GitWorktreeInfo> worktrees)
        => worktrees.Select(w => w.Branch).OfType<string>().ToHashSet(StringComparer.Ordinal);
}
