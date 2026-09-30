using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.Services;

/// <summary>
/// 作業ツリー（<c>git worktree</c>）まわりの純粋なロジック：引数の組み立てと既定の置き場所。
/// git を起動しないので単体テストできる。
///
/// <para><b>既定の置き場所はリポジトリの外</b>（<c>&lt;親&gt;\&lt;リポジトリ名&gt;.worktrees\&lt;ブランチ&gt;</c>）。
/// リポジトリの中（<c>.worktrees/</c> 等）に置くと、Loomo のワークスペースは祖先／子孫関係のフォルダーを
/// 同時に持てない（<c>WorkspaceService.AddFolder</c> が拒む）ので「フォルダーとして追加」ができず、
/// エクスプローラーのツリー・検索・ビルドにも別ブランチの複製が丸ごと紛れ込む。</para>
/// </summary>
public static class GitWorktreeArgs
{
    public static string[] ListArgs() => new[] { "worktree", "list", "--porcelain" };

    /// <summary>作成の引数。パスは <c>--</c> の後ろに置けない（git worktree add は pathspec を取らない）ので、
    /// ブランチ名・起点が <c>-</c> で始まる入力はここで弾く（オプションとして解釈させない）。</summary>
    public static string[] AddArgs(GitWorktreeAddRequest request)
    {
        var args = new List<string> { "worktree", "add" };
        switch (request.Mode)
        {
            case GitWorktreeAddMode.NewBranch:
                args.Add("-b");
                args.Add(Checked(request.Branch, "ブランチ名"));
                args.Add(request.Path);
                if (!string.IsNullOrWhiteSpace(request.StartPoint))
                    args.Add(Checked(request.StartPoint, "起点"));
                break;
            case GitWorktreeAddMode.ExistingBranch:
                args.Add(request.Path);
                args.Add(Checked(request.Branch, "ブランチ名"));
                break;
            case GitWorktreeAddMode.Detached:
                args.Add("--detach");
                args.Add(request.Path);
                args.Add(Checked(string.IsNullOrWhiteSpace(request.StartPoint) ? "HEAD" : request.StartPoint, "起点"));
                break;
        }
        return args.ToArray();
    }

    /// <summary>削除の引数。<paramref name="force"/> は未コミットの変更・未追跡ファイルがあっても消す
    /// （ロック中は git が二重の <c>-f</c> を要求するが、ここでは出さない——ロックは先に解除させる）。</summary>
    public static string[] RemoveArgs(string path, bool force)
        => force
            ? new[] { "worktree", "remove", "--force", path }
            : new[] { "worktree", "remove", path };

    /// <summary>削除の失敗が「変更・未追跡があるので <c>--force</c> が要る」によるものか
    /// （<c>fatal: '…' contains modified or untracked files, use --force to delete it</c>）。</summary>
    public static bool RemoveNeedsForce(string? message)
        => message is not null
           && (message.Contains("modified or untracked", StringComparison.OrdinalIgnoreCase)
               || message.Contains("use --force", StringComparison.OrdinalIgnoreCase));

    public static string[] PruneArgs() => new[] { "worktree", "prune", "--verbose" };

    public static string[] LockArgs(string path, string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? new[] { "worktree", "lock", path }
            : new[] { "worktree", "lock", "--reason", reason.Trim(), path };

    public static string[] UnlockArgs(string path) => new[] { "worktree", "unlock", path };

    /// <summary>未コミットの変更（未追跡を含む）を数えるための引数（作業ツリーの中で実行する）。</summary>
    public static string[] StatusArgs() => new[] { "--no-optional-locks", "status", "--porcelain" };

    /// <summary>
    /// 作業ツリーの既定の置き場所。<paramref name="mainWorktree"/>（メインの作業ツリー）の隣に
    /// <c>&lt;名前&gt;.worktrees</c> を作り、その下にブランチ名から作ったフォルダーを置く。
    /// </summary>
    public static string SuggestPath(string mainWorktree, string? branchOrName)
    {
        var trimmed = mainWorktree.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed) ?? trimmed;
        var repoName = Path.GetFileName(trimmed);
        var leaf = SanitizeFolderName(branchOrName);
        return Path.Combine(parent, $"{repoName}.worktrees", leaf.Length > 0 ? leaf : "worktree");
    }

    /// <summary>ブランチ名をフォルダー名にする。<c>feature/login</c> → <c>feature-login</c>。
    /// Windows で使えない文字は <c>-</c> に、末尾の <c>.</c>・空白は落とす（Explorer が扱えなくなる）。</summary>
    public static string SanitizeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            builder.Append(c is '/' or '\\' || invalid.Contains(c) ? '-' : c);
        var text = builder.ToString();
        while (text.Contains("--", StringComparison.Ordinal))
            text = text.Replace("--", "-", StringComparison.Ordinal);
        return text.Trim('-', '.', ' ');
    }

    /// <summary>ブランチ名・起点として渡してよい文字列か（空・先頭が <c>-</c>・空白入りは不可）。</summary>
    public static bool IsValidReference(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.StartsWith('-')
           && !value.Any(char.IsWhiteSpace);

    private static string Checked(string? value, string what)
        => IsValidReference(value)
            ? value!.Trim()
            : throw new ArgumentException($"{what}「{value}」は使えません。", nameof(value));
}
