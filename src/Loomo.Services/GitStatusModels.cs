using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>作業ツリー上の1ファイルの変更。</summary>
public sealed record GitChangeEntry(
    string Path,
    string? OrigPath,
    char IndexStatus,
    char WorkStatus,
    bool IsUntracked,
    bool IsConflicted);

/// <summary>コミットまたはコミット範囲の変更ファイル1件。</summary>
public sealed record GitCommitFileChange(char Status, string Path, string? OrigPath);

/// <summary>git status の取得結果スナップショット。</summary>
public sealed record GitStatusSnapshot
{
    public bool IsRepository { get; init; }
    public string Branch { get; init; } = "";
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public IReadOnlyList<GitChangeEntry> Staged { get; init; } = Array.Empty<GitChangeEntry>();
    public IReadOnlyList<GitChangeEntry> Unstaged { get; init; } = Array.Empty<GitChangeEntry>();
    public bool RebaseInProgress { get; init; }
    public bool MergeInProgress { get; init; }
    public bool CherryPickInProgress { get; init; }
    public bool OperationInProgress => RebaseInProgress || MergeInProgress || CherryPickInProgress;
}
