using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace sk0ya.Loomo.Services;

/// <summary>ワークツリー 1 つを片付けてよいか判断する材料（§24.55）。数えられなかった値は null。</summary>
/// <param name="SizeBytes">フォルダーの大きさ（ビルドの成果物・node_modules 込み——消して戻る容量そのもの）。</param>
/// <param name="LastCommitUtc">そのワークツリーの HEAD の最後のコミットの時刻。</param>
/// <param name="IsMerged">ブランチが既定ブランチへ取り込み済みか（デタッチ・既定ブランチ不明は null）。</param>
/// <param name="MergedInto">比べた既定ブランチの名前。</param>
public sealed record GitWorktreeUsage(
    string Path,
    long? SizeBytes,
    DateTime? LastCommitUtc,
    bool? IsMerged,
    string? MergedInto);

/// <summary>片付けの候補にする理由。</summary>
[Flags]
public enum GitWorktreeCleanupReason
{
    None = 0,
    /// <summary>ブランチが既定ブランチへ取り込み済み。</summary>
    Merged = 1,
    /// <summary>しばらく触っていない（最後のコミットも部屋を開いたのも <see cref="GitWorktreeCleanup.StaleAfter"/> より前）。</summary>
    Stale = 2,
    /// <summary>フォルダーが消えている（掃除で一覧から外すもの）。</summary>
    Vanished = 4,
}

/// <summary>
/// ワークツリーの片付けの判断と見せ方（純ロジック・テスト対象・§24.55）。
/// <para>VS Code 1.141 の Worktree Cleanup と同じ問い——「使われていないものを大きさつきで並べて、選んで消す」。
/// Loomo ではワークツリーごとに部屋（ワークスペース）があるので、「最後に触った」は最後のコミットと部屋を最後に開いた時刻の
/// 新しいほうで数える。消すときは部屋も一緒に片付く（<c>WorktreeRemoved</c> → <c>RemoveWorktreeRoom</c>）。</para>
/// </summary>
public static class GitWorktreeCleanup
{
    /// <summary>この日数より前から触っていなければ候補にする。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    /// <summary>メイン・bare・いまのワークツリー・ロック中は片付けられない（理由は <see cref="BlockedReason"/>）。</summary>
    public static bool CanClean(GitWorktreeInfo worktree)
        => worktree.CanRemove && !worktree.IsCurrent && !worktree.IsLocked;

    public static string? BlockedReason(GitWorktreeInfo worktree)
        => worktree.IsMain ? "メインのワークツリー"
            : worktree.IsBare ? "bare"
            : worktree.IsCurrent ? "いま開いている"
            : worktree.IsLocked ? "ロック中"
            : null;

    /// <summary>最後に触った時刻（最後のコミットと部屋を最後に開いた時刻の新しいほう）。どちらも分からなければ null。</summary>
    public static DateTime? LastActivity(GitWorktreeUsage? usage, DateTime? roomLastUsedUtc)
    {
        var commit = usage?.LastCommitUtc;
        if (commit is null) return roomLastUsedUtc;
        if (roomLastUsedUtc is null) return commit;
        return commit > roomLastUsedUtc ? commit : roomLastUsedUtc;
    }

    public static GitWorktreeCleanupReason Reasons(
        GitWorktreeInfo worktree, GitWorktreeUsage? usage, DateTime? roomLastUsedUtc, DateTime nowUtc)
    {
        if (!CanClean(worktree))
            return GitWorktreeCleanupReason.None;
        var reasons = GitWorktreeCleanupReason.None;
        if (worktree.IsPrunable)
            reasons |= GitWorktreeCleanupReason.Vanished;
        if (usage?.IsMerged == true)
            reasons |= GitWorktreeCleanupReason.Merged;
        if (LastActivity(usage, roomLastUsedUtc) is { } last && nowUtc - last > StaleAfter)
            reasons |= GitWorktreeCleanupReason.Stale;
        return reasons;
    }

    public static string ReasonText(GitWorktreeCleanupReason reasons, string? mergedInto)
    {
        var parts = new List<string>(3);
        if (reasons.HasFlag(GitWorktreeCleanupReason.Vanished)) parts.Add("フォルダーが無い");
        if (reasons.HasFlag(GitWorktreeCleanupReason.Merged))
            parts.Add(mergedInto is { Length: > 0 } into ? $"{into} に取り込み済み" : "取り込み済み");
        if (reasons.HasFlag(GitWorktreeCleanupReason.Stale)) parts.Add($"{StaleAfter.Days}日以上触っていない");
        return string.Join("・", parts);
    }

    /// <summary>「1.2 GB」「340 MB」「12 KB」。</summary>
    public static string FormatSize(long? bytes)
    {
        if (bytes is not { } b) return "";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = b;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 || value >= 100
            ? $"{value.ToString("0", CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>「3日前」「たった今」。</summary>
    public static string FormatAgo(DateTime? utc, DateTime nowUtc)
    {
        if (utc is not { } t) return "不明";
        var elapsed = nowUtc - t;
        return elapsed switch
        {
            { TotalMinutes: < 1 } => "たった今",
            { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes}分前",
            { TotalDays: < 1 } => $"{(int)elapsed.TotalHours}時間前",
            { TotalDays: < 60 } => $"{(int)elapsed.TotalDays}日前",
            _ => $"{(int)(elapsed.TotalDays / 30)}か月前",
        };
    }

    /// <summary>フォルダーの大きさ。シンボリックリンク・ジャンクションはたどらない（同じものを二度数えない・外へ出ない）。
    /// 読めないフォルダーは飛ばす。中止されたら <see cref="OperationCanceledException"/>。</summary>
    public static long MeasureDirectory(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = false,
        };
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
                {
                    if (entry is FileInfo file) total += file.Length;
                    else if (entry is DirectoryInfo sub) pending.Push(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        return total;
    }

    /// <summary><c>git branch --merged</c> の出力（<c>%(refname:short)</c> を 1 行ずつ）を名前の集合へ。</summary>
    public static HashSet<string> ParseMergedBranches(string? output)
        => (output ?? "").Split('\n')
            .Select(line => line.Trim().TrimStart('*', '+').Trim())
            .Where(line => line.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// ブランチの reflog の件名（<c>git reflog show --format=%gs</c>）から、そのブランチで作業した跡があるか。
    /// <c>git branch --merged</c> は自前のコミットが無いブランチ（作ったばかり）も「取り込み済み」に数えるので、
    /// 作成の 1 行しか無いものはそこから外す。reflog が無い（無効・期限切れ）ときは null＝判断できない。
    /// </summary>
    public static bool? ReflogShowsOwnWork(string? reflogSubjects)
    {
        var lines = (reflogSubjects ?? "").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0) return null;
        return lines.Any(line => !line.StartsWith("branch: Created from", StringComparison.Ordinal));
    }
}
