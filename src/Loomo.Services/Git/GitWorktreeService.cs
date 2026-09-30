using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.Services;

/// <summary>
/// 作業ツリー（<c>git worktree</c>）の照会・作成・削除・ロック・掃除と、作業ツリーの「いまの状態」の
/// スナップショット。作成・削除は他の変更系と同じく <see cref="GitMutationExecutor"/> を通す
/// （Git パネル・ペインが読み直す通知が出る）。
/// </summary>
public sealed class GitWorktreeService
{
    private readonly GitRootState _rootState;
    private readonly GitCommandRunner _runner;
    private readonly GitMutationExecutor _mutations;

    public GitWorktreeService(GitRootState rootState, GitCommandRunner runner, GitMutationExecutor mutations)
    {
        _rootState = rootState;
        _runner = runner;
        _mutations = mutations;
    }

    /// <summary>
    /// 作業ツリーの一覧。<paramref name="includeChangeCounts"/> なら各作業ツリーで <c>git status</c> を
    /// 並列に引いて未コミットの件数を付ける（比較基準の選択肢のように名前だけ要る場面では引かない）。
    /// 失敗（リポジトリでない・git が古くて worktree を知らない）は空一覧。
    /// </summary>
    public async Task<IReadOnlyList<GitWorktreeInfo>> ListAsync(bool includeChangeCounts)
    {
        var result = await _runner.RunAsync(GitWorktreeArgs.ListArgs()).ConfigureAwait(false);
        if (!result.Success) return Array.Empty<GitWorktreeInfo>();

        var parsed = GitWorktreeParser.Parse(result.Output);
        var current = CurrentWorktree(parsed, _rootState.CurrentRoot);
        var marked = parsed.Select(w => ReferenceEquals(w, current) ? w with { IsCurrent = true } : w).ToList();
        if (!includeChangeCounts) return marked;

        var counted = await Task.WhenAll(marked.Select(async w =>
            w with { ChangeCount = await CountChangesAsync(w).ConfigureAwait(false) })).ConfigureAwait(false);
        return counted;
    }

    /// <summary>
    /// いま Git 操作の対象になっているフォルダーを含む作業ツリー。入れ子（リポジトリの中に置いた作業ツリー）
    /// ではメインも外側で一致するので、<b>いちばん深い</b>一致を選ぶ。
    /// </summary>
    public static GitWorktreeInfo? CurrentWorktree(IReadOnlyList<GitWorktreeInfo> worktrees, string? currentRoot)
    {
        if (string.IsNullOrEmpty(currentRoot)) return null;
        return worktrees
            .Where(w => !w.IsBare && WorkspacePaths.IsWithin(w.Path, currentRoot))
            .OrderByDescending(w => w.Path.Length)
            .FirstOrDefault();
    }

    public Task<GitCommandResult> AddAsync(GitWorktreeAddRequest request)
    {
        string[] args;
        try
        {
            args = GitWorktreeArgs.AddArgs(request);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(new GitCommandResult(-1, "", ex.Message));
        }
        return _mutations.ExecuteAsync(args);
    }

    public Task<GitCommandResult> RemoveAsync(string path, bool force)
        => _mutations.ExecuteAsync(GitWorktreeArgs.RemoveArgs(path, force));

    public Task<GitCommandResult> PruneAsync() => _mutations.ExecuteAsync(GitWorktreeArgs.PruneArgs());

    public Task<GitCommandResult> LockAsync(string path, string? reason)
        => _mutations.ExecuteAsync(GitWorktreeArgs.LockArgs(path, reason));

    public Task<GitCommandResult> UnlockAsync(string path)
        => _mutations.ExecuteAsync(GitWorktreeArgs.UnlockArgs(path));

    /// <summary>
    /// 作業ツリーの「いまの状態」（未コミットの編集・ステージ・<b>未追跡</b>を含む。ignore されたものは除く）を
    /// tree オブジェクトに固めて返す。<b>相手の作業ツリーにもインデックスにも一切書かない</b>——
    /// インデックスを一時ファイルへ複製し、<c>GIT_INDEX_FILE</c> をそこへ向けて <c>add -A</c> → <c>write-tree</c>
    /// する（複製元の stat 情報が効くので、変わっていないファイルは読み直さない）。
    ///
    /// <para><c>git stash create</c> を使わないのは、未追跡ファイルを含められないため——別の作業ツリーで
    /// 新しく作ったファイルが比較に出ないのでは「向こうの作業中の状態と比べる」にならない。
    /// commit ではなく tree を返すのは、日時が入らず<b>同じ内容なら同じハッシュ</b>になるため
    /// （読み直しのたびに基準が変わったことにならない）。オブジェクトは作業ツリー間で共有されるので、
    /// 得たハッシュはどの作業ツリーの <c>git diff</c> にもそのまま渡せる。</para>
    /// </summary>
    public async Task<GitWorktreeSnapshot> SnapshotAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(worktreePath))
            return new GitWorktreeSnapshot(null, $"ワークツリーのフォルダーがありません: {worktreePath}");

        var indexQuery = await _runner.RunInAsync(
            worktreePath, null, null, cancellationToken, "rev-parse", "--git-path", "index").ConfigureAwait(false);
        if (!indexQuery.Success)
            return new GitWorktreeSnapshot(null, $"ワークツリーを読めませんでした: {indexQuery.Message}");
        var indexPath = indexQuery.Output.Trim();
        if (!Path.IsPathRooted(indexPath))
            indexPath = Path.Combine(worktreePath, indexPath);

        var temp = Path.Combine(Path.GetTempPath(), $"loomo-worktree-{Guid.NewGuid():N}.index");
        try
        {
            if (File.Exists(indexPath))
                File.Copy(indexPath, temp);
            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = temp };

            var add = await _runner.RunInAsync(
                worktreePath, environment, null, cancellationToken, "add", "--all").ConfigureAwait(false);
            if (!add.Success)
                return new GitWorktreeSnapshot(null, $"ワークツリーの状態を固められませんでした: {add.Message}");

            var tree = await _runner.RunInAsync(
                worktreePath, environment, null, cancellationToken, "write-tree").ConfigureAwait(false);
            var hash = tree.Output.Trim();
            return tree.Success && hash.Length > 0
                ? new GitWorktreeSnapshot(hash, null)
                : new GitWorktreeSnapshot(null, $"ワークツリーの状態を固められませんでした: {tree.Message}");
        }
        catch (IOException ex)
        {
            return new GitWorktreeSnapshot(null, $"インデックスを複製できませんでした: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new GitWorktreeSnapshot(null, $"インデックスを複製できませんでした: {ex.Message}");
        }
        finally
        {
            TryDelete(temp);
            TryDelete(temp + ".lock");
        }
    }

    /// <summary>ref（ブランチ・タグ・ハッシュ・<c>HEAD~2</c> 等）をコミットのハッシュへ。無ければ null。</summary>
    public async Task<string?> ResolveCommitAsync(string reference, string? workingDirectory = null)
    {
        var args = GitCompareArgs.VerifyCommitArgs(reference);
        var result = workingDirectory is null
            ? await _runner.RunAsync(args).ConfigureAwait(false)
            : await _runner.RunInAsync(workingDirectory, null, null, CancellationToken.None, args).ConfigureAwait(false);
        var hash = result.Output.Trim();
        return result.Success && hash.Length > 0 ? hash : null;
    }

    private async Task<int?> CountChangesAsync(GitWorktreeInfo worktree)
    {
        if (!worktree.Exists || worktree.IsBare) return null;
        var result = await _runner.RunInAsync(
            worktree.Path, null, null, CancellationToken.None, GitWorktreeArgs.StatusArgs()).ConfigureAwait(false);
        if (!result.Success) return null;
        return result.Output.Split('\n').Count(line => line.TrimEnd('\r').Length > 0);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* 一時ファイルの取り残しは害が無い */ }
        catch (UnauthorizedAccessException) { }
    }
}
