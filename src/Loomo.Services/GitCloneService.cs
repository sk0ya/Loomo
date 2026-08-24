using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>クローンの結果。成功したらワークスペースへ開けるフルパスが入る。</summary>
public sealed record GitCloneResult(bool Success, string TargetPath, string Message);

/// <summary>
/// リポジトリのクローン。<b>他の git 操作と違ってリポジトリの外で走る</b>——親フォルダーを
/// 作業ディレクトリにして <c>git clone</c> を実行するので、ワークスペースの現在の対象
/// （<see cref="GitRootState"/>）には一切触らない。
///
/// <para>認証は他の操作と同じ扱い（<c>GIT_TERMINAL_PROMPT=0</c>）。資格情報ヘルパー
/// （Git Credential Manager 等）が入っていれば通り、対話が要るとその場で失敗する
/// ——端末のプロンプトを裏で待って固まるより、理由を返して終わる方を選んでいる。</para>
/// </summary>
public sealed class GitCloneService
{
    /// <summary>大きなリポジトリでも刈られないクローン用のタイムアウト。</summary>
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(30);

    private readonly GitCommandRunner _runner;

    public GitCloneService(GitCommandRunner runner) => _runner = runner;

    /// <summary>
    /// <paramref name="url"/> を <paramref name="parentDirectory"/> の下の
    /// <paramref name="folderName"/>（省略時は URL 由来）へクローンする。
    /// 既に同名のフォルダーがあるときは<b>実行しない</b>——git 自身も拒むが、
    /// その場合のメッセージが「destination path already exists」だけで分かりにくいため先に弾く。
    /// </summary>
    public async Task<GitCloneResult> CloneAsync(
        string url, string parentDirectory, string? folderName = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedUrl = url?.Trim() ?? "";
        if (trimmedUrl.Length == 0)
            return new GitCloneResult(false, "", "リポジトリの URL を入力してください。");
        if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
            return new GitCloneResult(false, "", $"フォルダーがありません: {parentDirectory}");

        var name = string.IsNullOrWhiteSpace(folderName) ? FolderNameFrom(trimmedUrl) : folderName.Trim();
        if (name.Length == 0)
            return new GitCloneResult(false, "", $"URL からフォルダー名を決められません: {trimmedUrl}");

        var target = Path.GetFullPath(Path.Combine(parentDirectory, name));
        if (Directory.Exists(target) || File.Exists(target))
            return new GitCloneResult(false, target, $"既に存在します: {target}");

        var result = await _runner.RunInAsync(
            parentDirectory, extraEnvironment: null, CloneTimeout, cancellationToken,
            "clone", "--progress", trimmedUrl, name).ConfigureAwait(false);

        return result.Success
            ? new GitCloneResult(true, target, $"{name} をクローンしました。")
            : new GitCloneResult(false, target, result.Message);
    }

    /// <summary>
    /// URL の末尾からフォルダー名を決める（git 自身と同じ規則：末尾の "/" と ".git" を落とす）。
    /// <c>https://host/org/repo.git</c> も <c>git@host:org/repo.git</c> も <c>repo</c> になる。
    /// </summary>
    public static string FolderNameFrom(string url)
    {
        var value = url.Trim().TrimEnd('/', '\\');
        var cut = value.LastIndexOfAny(new[] { '/', '\\', ':' });
        var name = cut >= 0 ? value[(cut + 1)..] : value;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Trim();
    }
}
