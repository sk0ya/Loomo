using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>コミットのビルド（CI）の様子（§24.93）。</summary>
public enum CommitBuildState { Pending, Success, Failure }

/// <summary>
/// コミット 1 つに対するビルド 1 回（GitHub Actions の実行・Azure Pipelines のビルド）。
/// 同じコミットに複数の実行（ワークフロー・パイプライン）が付く。
/// </summary>
public sealed record CommitBuild(string Sha, string Name, CommitBuildState State, string? Url);

/// <summary>コミット一覧の行に出す印（§24.93）。純関数。</summary>
public static class CommitBuildMarks
{
    public const string SuccessMark = "✓";
    public const string FailureMark = "✗";
    public const string PendingMark = "◌";

    /// <summary>
    /// コミットごとにまとめる：1 つでも落ちていれば失敗、1 つでも終わっていなければ待ち、全部通れば成功（PR のチェックと同じ・§24.39）。
    /// 同じ名前の実行が何度もある（やり直した）なら新しい方だけを数える——並びは新しい順で渡す。
    /// </summary>
    public static IReadOnlyDictionary<string, CommitBuildSummary> Summarize(IEnumerable<CommitBuild> newestFirst)
    {
        var result = new Dictionary<string, CommitBuildSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in newestFirst.Where(b => b.Sha.Length > 0).GroupBy(b => b.Sha, StringComparer.OrdinalIgnoreCase))
        {
            var latest = group.GroupBy(b => b.Name, StringComparer.Ordinal).Select(g => g.First()).ToList();
            var state = latest.Any(b => b.State == CommitBuildState.Failure) ? CommitBuildState.Failure
                : latest.Any(b => b.State == CommitBuildState.Pending) ? CommitBuildState.Pending
                : CommitBuildState.Success;
            // 開く先は「見たいもの」：落ちたもの → 動いているもの → どれか。
            var open = latest.FirstOrDefault(b => b.State == state && b.Url is { Length: > 0 }) ?? latest.FirstOrDefault(b => b.Url is { Length: > 0 });
            result[group.Key] = new CommitBuildSummary(state, latest, open?.Url);
        }
        return result;
    }

    public static string Mark(CommitBuildState state) => state switch
    {
        CommitBuildState.Success => SuccessMark,
        CommitBuildState.Failure => FailureMark,
        _ => PendingMark,
    };

    /// <summary>印に添える説明（実行ごとに 1 行）。</summary>
    public static string Describe(CommitBuildSummary summary)
    {
        var head = summary.State switch
        {
            CommitBuildState.Success => "ビルド：すべて通っています",
            CommitBuildState.Failure => "ビルド：落ちています",
            _ => "ビルド：実行中・待ち",
        };
        var lines = summary.Builds.Select(b => $"{Mark(b.State)} {b.Name}");
        return head + Environment.NewLine + string.Join(Environment.NewLine, lines) +
               (summary.Url is null ? "" : Environment.NewLine + "クリックで実行のページを開く");
    }
}

/// <summary>コミット 1 つぶんのまとめ。<see cref="Url"/> は印を押したときに開く実行のページ。</summary>
public sealed record CommitBuildSummary(CommitBuildState State, IReadOnlyList<CommitBuild> Builds, string? Url);
