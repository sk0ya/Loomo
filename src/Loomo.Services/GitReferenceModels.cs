using System;

namespace sk0ya.Loomo.Services;

/// <summary>ブランチ1件。</summary>
public sealed record GitBranchInfo(string Name, bool IsCurrent, bool IsRemote, string? Upstream)
{
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public bool UpstreamGone { get; init; }
    /// <summary>先頭コミットの日時。相対表記はビュー側で生成する。</summary>
    public DateTimeOffset? LastCommit { get; init; }
}

/// <summary>タグ1件。</summary>
public sealed record GitTagInfo(string Name, string TargetShortHash, string? Subject, bool IsAnnotated, string? Date);

/// <summary>サブモジュール1件。</summary>
public sealed record GitSubmoduleInfo(
    string Path,
    string Hash,
    string? Describe,
    bool IsUninitialized,
    bool HasDivergedCommit,
    bool HasMergeConflict)
{
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;
    public string? StatusLabel => IsUninitialized ? "未初期化"
        : HasMergeConflict ? "コンフリクト"
        : HasDivergedCommit ? "差分あり"
        : null;
}
