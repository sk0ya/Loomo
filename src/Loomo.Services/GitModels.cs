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

/// <summary>コミット／コミット範囲の変更ファイル1件（git の name-status 1行）。</summary>
/// <param name="Status">状態文字（A/M/D/R 等。R100 などのスコアは落とす）。</param>
/// <param name="Path">リポジトリルートからの相対パス（リネーム時は新パス）。</param>
/// <param name="OrigPath">リネーム・コピー時の元パス（それ以外は null）。</param>
public sealed record GitCommitFileChange(char Status, string Path, string? OrigPath);

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

/// <summary>タグ1件（git for-each-ref refs/tags の1行）。</summary>
/// <param name="Name">タグ名。</param>
/// <param name="TargetShortHash">タグが指す先のコミット（注釈付きタグは参照解決後）の短縮ハッシュ。</param>
/// <param name="Subject">注釈付きタグならタグメッセージの1行目、軽量タグなら指す先コミットの件名。</param>
/// <param name="IsAnnotated">注釈付きタグ（git tag -a）か。</param>
/// <param name="Date">作成日時（注釈付きはタグ作成日、軽量は指す先コミット日）。</param>
public sealed record GitTagInfo(string Name, string TargetShortHash, string? Subject, bool IsAnnotated, string? Date);

/// <summary>
/// サブモジュール1件（<c>git submodule status</c> の1行）。先頭1文字の状態フラグを
/// <see cref="IsUninitialized"/>／<see cref="HasDivergedCommit"/>／<see cref="HasMergeConflict"/> に分解する。
/// </summary>
/// <param name="Path">リポジトリルートからの相対パス。</param>
/// <param name="Hash">登録されているコミットのフルハッシュ。</param>
/// <param name="Describe">括弧内の説明（例: <c>heads/main</c>）。取得できなければ null。</param>
/// <param name="IsUninitialized">未初期化（先頭が '-'。<c>submodule init</c>/<c>update</c> 未実行）か。</param>
/// <param name="HasDivergedCommit">チェックアウト済みコミットが親リポジトリの登録コミットと異なる（先頭が '+'）か。</param>
/// <param name="HasMergeConflict">マージ未解決（先頭が 'U'）か。</param>
public sealed record GitSubmoduleInfo(
    string Path,
    string Hash,
    string? Describe,
    bool IsUninitialized,
    bool HasDivergedCommit,
    bool HasMergeConflict)
{
    /// <summary>短縮ハッシュ（表示用）。</summary>
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;

    /// <summary>UI 表示用の状態ラベル。正常（変更なし）なら null。</summary>
    public string? StatusLabel => IsUninitialized ? "未初期化"
        : HasMergeConflict ? "コンフリクト"
        : HasDivergedCommit ? "差分あり"
        : null;
}

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

/// <summary>スタッシュ1件（git stash list の1行）。</summary>
/// <param name="Ref">stash の参照（例: <c>stash@{0}</c>）。apply/pop/drop の対象に使う。</param>
/// <param name="Description">説明（例: <c>WIP on main: abc1234 件名</c>）。</param>
public sealed record GitStashEntry(string Ref, string Description);

/// <summary>git reset のモード。</summary>
public enum GitResetMode
{
    Soft,
    Mixed,
    Hard
}

/// <summary>
/// git merge の戦略。<see cref="Default"/> は現状動作（fast-forward 可能ならそのまま、
/// 不可能ならマージコミットを作成／未指定時のコミットメッセージ編集は省略）。
/// </summary>
public enum GitMergeStrategy
{
    /// <summary>--no-edit のみ（fast-forward できればそのまま進める、既定の git 挙動）。</summary>
    Default,

    /// <summary>--ff-only。fast-forward できないときは失敗させる（マージコミットを作らない）。</summary>
    FastForwardOnly,

    /// <summary>--no-ff --no-edit。fast-forward可能でも必ずマージコミットを作る。</summary>
    NoFastForward,

    /// <summary>--squash。作業ツリー・インデックスへ変更を取り込むだけでコミットはしない
    /// （呼び出し側で別途コミットする必要がある）。</summary>
    Squash
}

/// <summary>インタラクティブリベースの todo アクション（git のリベースコマンドと同じ意味）。</summary>
public enum RebaseAction
{
    Pick,
    Reword,
    Edit,
    Squash,
    Fixup,
    Drop
}

/// <summary>インタラクティブリベース計画の1コミット。</summary>
public sealed record RebasePlanEntry(string Hash, string ShortHash, string Subject, RebaseAction Action)
{
    public RebasePlanEntry WithAction(RebaseAction action) => this with { Action = action };
}

/// <summary>
/// git blame の1行（<c>git blame --line-porcelain</c> の1エントリ）。
/// </summary>
/// <param name="Hash">その行を最後に変更したコミットのフルハッシュ。</param>
/// <param name="ShortHash">短縮ハッシュ（表示用）。</param>
/// <param name="Author">著者名。</param>
/// <param name="AuthorDate">著者日時（著者のタイムゾーンでの <c>yyyy-MM-dd HH:mm</c>）。</param>
/// <param name="OriginalLineNumber">そのコミット時点（元ファイル）での行番号。</param>
/// <param name="FinalLineNumber">現在のファイルでの行番号（1始まり）。</param>
/// <param name="Content">行の内容。</param>
public sealed record GitBlameLine(
    string Hash,
    string ShortHash,
    string Author,
    string AuthorDate,
    int OriginalLineNumber,
    int FinalLineNumber,
    string Content);
