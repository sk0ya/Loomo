using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>この部屋のターミナルで動いている Claude Code 1つ：状態と、どこで作業しているかの手がかり。</summary>
internal sealed record WorktreeAgentSighting(ClaudeAgentState State, string? TranscriptPath, string? WorkingDirectory);

/// <summary>
/// ワークツリー一覧の行に「そこで Claude Code が動いている」印を付ける（§24.17.2）。GitKraken 12.3 の
/// 「ワークツリーごとの WIP ノード」に当たる——並行作業のどれが途中で、どれが人間の番かを一覧で見る。
/// <para>どのワークツリーかは2つの手がかりで決める。(1) セッション記録のフォルダー名：Claude は自分の作業フォルダーを
/// slug にしたフォルダーへ記録するので、<c>claude --worktree</c> のようにシェルの cwd が本体のままでも、
/// 記録はワークツリー側に付く。(2) 記録がまだ無い（フックを入れていない）ならターミナルの cwd。
/// cwd はワークツリーが入れ子（本体の <c>.claude/worktrees/</c> の下）になり得るので、<b>いちばん深い</b>ものを採る。</para>
/// <para>同じワークツリーに複数居たら、人間の番が先（<see cref="ClaudeAgentState"/> の並び）の状態を出す。</para>
/// </summary>
internal static class WorktreeAgentMarks
{
    /// <summary>ワークツリーのパス（<see cref="GitWorktreeInfo.Path"/> そのもの）→ 行に出す印。</summary>
    public static IReadOnlyDictionary<string, string> For(
        IReadOnlyList<GitWorktreeInfo> worktrees, IEnumerable<WorktreeAgentSighting> agents)
    {
        var states = new Dictionary<string, ClaudeAgentState>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            if (Locate(worktrees, agent) is not { } worktree) continue;
            if (!states.TryGetValue(worktree.Path, out var current) || agent.State < current)
                states[worktree.Path] = agent.State;
        }
        return states.ToDictionary(p => p.Key, p => Mark(p.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>行に出す短い印（§38.8 の一覧と同じ記号）。</summary>
    public static string Mark(ClaudeAgentState state) => state switch
    {
        ClaudeAgentState.NeedsApproval => "● 承認待ち",
        ClaudeAgentState.TurnEnded => "✳ 入力待ち",
        ClaudeAgentState.Waiting => "Claude",
        _ => "◐ 作業中",
    };

    private static GitWorktreeInfo? Locate(IReadOnlyList<GitWorktreeInfo> worktrees, WorktreeAgentSighting agent)
    {
        var candidates = worktrees.Where(w => !w.IsBare && !w.IsPrunable).ToList();
        if (agent.TranscriptPath is { Length: > 0 } transcript
            && Path.GetFileName(Path.GetDirectoryName(transcript)) is { Length: > 0 } slug)
        {
            // slug は英数字以外を潰すので別フォルダーと衝突し得るが、同じリポジトリのワークツリー同士で
            // 衝突するのは名前の記号だけが違う場合に限られる。完全一致だけを採る。
            var bySlug = candidates.FirstOrDefault(w =>
                string.Equals(ClaudeTranscriptLocator.Slug(w.Path), slug, StringComparison.OrdinalIgnoreCase));
            if (bySlug is not null) return bySlug;
        }
        if (agent.WorkingDirectory is not { Length: > 0 } cwd) return null;
        var full = Normalize(cwd);
        return candidates
            .Select(w => (Worktree: w, Root: Normalize(w.Path)))
            .Where(c => IsSameOrUnder(full, c.Root))
            .OrderByDescending(c => c.Root.Length)
            .Select(c => c.Worktree)
            .FirstOrDefault();
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static bool IsSameOrUnder(string path, string root)
        => path.Equals(root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
