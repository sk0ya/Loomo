using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>git コマンド1回の実行結果。</summary>
public sealed record GitCommandResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;

    /// <summary>UI 表示用メッセージ。git はエラー・進捗を stderr へ出すので stderr 優先。</summary>
    public string Message
    {
        get
        {
            var err = Error.Trim();
            if (err.Length > 0) return err;
            return Output.Trim();
        }
    }
}

/// <summary>作業ツリー上の1ファイルの変更（git status --porcelain=v2 の1エントリ）。</summary>
/// <param name="Path">リポジトリルートからの相対パス。</param>
/// <param name="OrigPath">リネーム時の元パス（それ以外は null）。</param>
/// <param name="IndexStatus">インデックス側の状態（X）。変更なしは '.'。</param>
/// <param name="WorkStatus">作業ツリー側の状態（Y）。変更なしは '.'。</param>
/// <param name="IsUntracked">未追跡ファイルか。</param>
/// <param name="IsConflicted">マージ未解決（コンフリクト）か。</param>
public sealed record GitChangeEntry(
    string Path,
    string? OrigPath,
    char IndexStatus,
    char WorkStatus,
    bool IsUntracked,
    bool IsConflicted);

/// <summary>git status の取得結果スナップショット。</summary>
public sealed record GitStatusSnapshot
{
    /// <summary>ワークスペースルートが git リポジトリか。</summary>
    public bool IsRepository { get; init; }

    /// <summary>現在のブランチ名。デタッチ HEAD なら "(detached)"。</summary>
    public string Branch { get; init; } = "";

    /// <summary>追跡上流ブランチ（例: origin/main）。未設定なら null。</summary>
    public string? Upstream { get; init; }

    public int Ahead { get; init; }
    public int Behind { get; init; }

    /// <summary>ステージ済みの変更。</summary>
    public IReadOnlyList<GitChangeEntry> Staged { get; init; } = Array.Empty<GitChangeEntry>();

    /// <summary>未ステージの変更（未追跡・コンフリクト含む）。</summary>
    public IReadOnlyList<GitChangeEntry> Unstaged { get; init; } = Array.Empty<GitChangeEntry>();

    public bool RebaseInProgress { get; init; }
    public bool MergeInProgress { get; init; }
    public bool CherryPickInProgress { get; init; }

    /// <summary>rebase / merge / cherry-pick のいずれかが進行中（続行・中止の操作対象がある）か。</summary>
    public bool OperationInProgress => RebaseInProgress || MergeInProgress || CherryPickInProgress;
}

/// <summary>ブランチ1件。</summary>
/// <param name="Name">短縮名（ローカル: main、リモート: origin/main）。</param>
public sealed record GitBranchInfo(string Name, bool IsCurrent, bool IsRemote, string? Upstream);

/// <summary>
/// git log --graph の1行。コミット行はメタ情報を持ち、枝の継続だけの行（"| /" 等）は
/// <see cref="Hash"/> が null になる。
/// </summary>
public sealed record GitLogRow(
    string Graph,
    string? Hash,
    string? ShortHash,
    string? Author,
    string? Date,
    string? Refs,
    string? Subject)
{
    public bool IsCommit => Hash is not null;
}

/// <summary>git reset のモード。</summary>
public enum GitResetMode
{
    Soft,
    Mixed,
    Hard
}
