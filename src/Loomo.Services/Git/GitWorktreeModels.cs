using System;
using System.IO;

namespace sk0ya.Loomo.Services;

/// <summary>
/// <c>git worktree list --porcelain</c> の1件。<see cref="Path"/> は Windows 表記へ正規化済み
/// （git は <c>C:/work/app</c> とスラッシュで返すので、そのまま比べるとワークスペースのパスと一致しない）。
/// </summary>
public sealed record GitWorktreeInfo
{
    public required string Path { get; init; }

    /// <summary>チェックアウトしているコミット（空リポジトリの未出生ブランチでは空）。</summary>
    public string Head { get; init; } = "";

    /// <summary>チェックアウトしているブランチの短縮名（<c>refs/heads/</c> を落としたもの）。デタッチなら null。</summary>
    public string? Branch { get; init; }

    public bool IsDetached { get; init; }
    public bool IsBare { get; init; }

    /// <summary>一覧の先頭＝メインの作業ツリー（<c>.git</c> ディレクトリを持つ本体）。削除できない。</summary>
    public bool IsMain { get; init; }

    public bool IsLocked { get; init; }
    public string? LockReason { get; init; }

    /// <summary>フォルダーが消えていて <c>git worktree prune</c> で掃除される対象。</summary>
    public bool IsPrunable { get; init; }
    public string? PrunableReason { get; init; }

    /// <summary>いま Git 操作の対象になっているフォルダーを含む作業ツリーか（<see cref="GitWorktreeService"/> が付ける）。</summary>
    public bool IsCurrent { get; init; }

    /// <summary>未コミットの変更（未追跡を含む）の件数。数えられなかった（フォルダーが無い等）なら null。</summary>
    public int? ChangeCount { get; init; }

    public string ShortHead => Head.Length > 7 ? Head[..7] : Head;

    /// <summary>一覧・比較基準の選択肢に出す名前。ブランチ名、デタッチなら <c>(detached) abc1234</c>。</summary>
    public string DisplayName => IsBare ? "(bare)"
        : Branch ?? (Head.Length > 0 ? $"(detached) {ShortHead}" : "(detached)");

    /// <summary>フォルダー名（パスの末尾）。ブランチ名と並べて「どこに置いたか」を見分ける。</summary>
    public string FolderName => System.IO.Path.GetFileName(
        Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    public bool IsDirty => ChangeCount is > 0;

    /// <summary>削除できるか。メインと bare は git 自身が拒む。</summary>
    public bool CanRemove => !IsMain && !IsBare;

    /// <summary>フォルダーとして開ける（＝実在する）か。</summary>
    public bool Exists => !IsPrunable && Directory.Exists(Path);

    public bool IsSamePath(string? other)
        => other is not null && string.Equals(
            Normalize(Path), Normalize(other), StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path;
        }
    }
}

/// <summary>作業ツリーの作り方。</summary>
public enum GitWorktreeAddMode
{
    /// <summary>新しいブランチを起点から切って置く（<c>worktree add -b &lt;new&gt; &lt;path&gt; &lt;start&gt;</c>）。</summary>
    NewBranch,

    /// <summary>既存のブランチを置く（<c>worktree add &lt;path&gt; &lt;branch&gt;</c>）。リモートにしか無い名前なら
    /// git が追跡ブランチを作る。</summary>
    ExistingBranch,

    /// <summary>コミットをデタッチで置く（<c>worktree add --detach &lt;path&gt; &lt;commit&gt;</c>）。</summary>
    Detached,
}

/// <summary>作業ツリー作成の要求。</summary>
public sealed record GitWorktreeAddRequest(
    GitWorktreeAddMode Mode, string Path, string? Branch, string? StartPoint);

/// <summary>
/// 作業ツリーの「いまの状態」を固めた tree。<see cref="Tree"/> は未コミット・未追跡（ignore 以外）を含む
/// 内容の tree ハッシュで、同じ内容なら同じハッシュになる（一覧の同一判定がちらつかない）。
/// </summary>
public sealed record GitWorktreeSnapshot(string? Tree, string? Error)
{
    public bool Success => Tree is not null;
}
