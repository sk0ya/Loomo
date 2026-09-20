using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Git;

namespace sk0ya.Loomo.Services;

/// <summary>コミットログ、コミット内容、コミット範囲の差分を照会する。</summary>
public sealed class GitHistoryService
{
    private readonly GitCommandRunner _runner;

    public GitHistoryService(GitCommandRunner runner) => _runner = runner;

    public Task<IReadOnlyList<GitLogRow>> GetLogAsync(
        string? branchRef = null, int limit = 300, int skip = 0, string? pathFilter = null) =>
        GetLogAsync(new GitLogQuery
        {
            BranchRef = branchRef,
            Limit = limit,
            Skip = skip,
            PathFilter = pathFilter,
        });

    /// <summary>
    /// 条件付きでコミットログを引く。絞り込み（作者・本文・日付）は <paramref name="query"/> 経由で
    /// <b>git に渡る</b>ので、読み込み済みのページの外にある古いコミットも対象になる。
    ///
    /// <para><c>--follow</c> は「1つの pathspec」でしか使えず、指定に反すると git は
    /// <c>fatal: --follow requires exactly one pathspec</c> で終わる。<see cref="GitLogQuery"/> の側で
    /// パスがあるときだけ付けているが、それでもリネーム追跡が使えない git 構成はあり得るので、
    /// 失敗したら <c>--follow</c> を落として引き直す（履歴が丸ごと空になるより、追跡なしで出す方がよい）。</para>
    /// </summary>
    public async Task<IReadOnlyList<GitLogRow>> GetLogAsync(GitLogQuery query)
        => (await GetLogResultAsync(query).ConfigureAwait(false)).Rows;

    /// <summary>ログを引き、<b>空一覧が「0 件」なのか「git が失敗した」なのかも返す</b>。
    /// 呼び出し側（キャッシュ）が失敗を成功した答えとして覚え込まないための区別。</summary>
    public async Task<(IReadOnlyList<GitLogRow> Rows, bool Failed)> GetLogResultAsync(GitLogQuery query)
    {
        var result = await _runner.RunAsync(query.ToArguments()).ConfigureAwait(false);
        if (!result.Success && query.FollowRenames)
            result = await _runner.RunAsync((query with { FollowRenames = false }).ToArguments())
                .ConfigureAwait(false);
        return result.Success
            ? (GitLogParser.Parse(result.Output), false)
            : (Array.Empty<GitLogRow>(), true);
    }

    /// <summary>リネームを追ったときの「コミットごとのパス」表を作る（<see cref="GitRenameTrail"/>）。
    /// 引けなかったら空の表を返す——その場合は呼び出し側がいまのパスのまま扱う。</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetRenameTrailAsync(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return new Dictionary<string, string>();
        var result = await _runner
            .RunAsync("log", "--follow", "--format=%H", "--name-status", "--", relativePath)
            .ConfigureAwait(false);
        return result.Success
            ? GitRenameTrail.Parse(result.Output, relativePath)
            : new Dictionary<string, string>();
    }

    public async Task<string> GetCommitSummaryAsync(string hash)
    {
        // --stat ではなく --numstat。--stat は幅に合わせてパスを ".../Views/Foo.xaml" と
        // 省略するので、変更ファイル一覧をフォルダ構造へ組み直せない（CommitSummary が解析する）。
        // 書式も fuller ではなく区切り文字つきの1レコードにする——Git ペインの詳細は
        // グラフの右の細い列なので、fuller の "AuthorDate: ..." が何行にも折り返して
        // 肝心のコミットコメントを画面外へ押し出してしまう（並べ替えは CommitSummary の仕事）。
        var result = await _runner
            .RunAsync("show", "--numstat", "--date=format:%Y-%m-%d %H:%M", "--format=" + CommitSummary.Format, hash)
            .ConfigureAwait(false);
        return result.Success ? result.Output : result.Message;
    }

    public async Task<string> GetCommitPatchAsync(string hash)
    {
        var result = await _runner.RunAsync("show", hash).ConfigureAwait(false);
        return result.Success ? result.Output : result.Message;
    }

    public async Task<IReadOnlyList<GitCommitFileChange>> GetRangeChangesAsync(
        string? fromHash, string toHash)
    {
        var result = fromHash is null
            ? await _runner.RunAsync("diff-tree", "--root", "-r", "-m", "--first-parent",
                "--no-commit-id", "--name-status", toHash).ConfigureAwait(false)
            : await _runner.RunAsync("diff", "--name-status", fromHash, toHash).ConfigureAwait(false);
        return result.Success
            ? GitNameStatusParser.Parse(result.Output)
            : Array.Empty<GitCommitFileChange>();
    }

    public async Task<string> GetRangeFileDiffAsync(
        string? fromHash, string toHash, GitCommitFileChange file, int contextLines = 3)
    {
        var unified = $"--unified={contextLines}";
        var args = new List<string>();
        if (fromHash is null)
            args.AddRange(new[]
                { "diff-tree", "--root", "-p", unified, "-m", "--first-parent", "--no-commit-id", toHash });
        else
            args.AddRange(new[] { "diff", unified, fromHash, toHash });
        args.Add("--");
        if (file.OrigPath is not null)
            args.Add(file.OrigPath);
        args.Add(file.Path);

        var result = await _runner.RunAsync(args.ToArray()).ConfigureAwait(false);
        return result.Success ? result.Output : result.Message;
    }
}
