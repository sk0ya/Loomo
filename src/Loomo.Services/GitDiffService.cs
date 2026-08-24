using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>作業ツリー差分、競合ステージ、インデックス向けパッチを扱う。</summary>
public sealed class GitDiffService
{
    private readonly GitRootState _rootState;
    private readonly GitCommandRunner _runner;
    private readonly GitMutationExecutor _mutations;

    public GitDiffService(
        GitRootState rootState, GitCommandRunner runner, GitMutationExecutor mutations)
    {
        _rootState = rootState;
        _runner = runner;
        _mutations = mutations;
    }

    public async Task<string> GetDiffTextAsync(
        GitChangeEntry entry, bool staged, int contextLines = 3)
    {
        if (entry.IsUntracked)
        {
            if (_rootState.CurrentRoot is null) return "";
            var fullPath = Path.Combine(_rootState.CurrentRoot, entry.Path);
            try
            {
                var content = File.Exists(fullPath)
                    ? await File.ReadAllTextAsync(fullPath).ConfigureAwait(false)
                    : "";
                return BuildUntrackedPatch(entry.Path, content);
            }
            catch (Exception exception)
            {
                return $"# 読み取り失敗: {exception.Message}";
            }
        }

        // pathspec をリテラル扱いにする（git の pathspec は既定でグロブが効くので、これが無いと
        // "a[1].txt" の差分に "a1.txt" が混ざる）。GitCompareArgs 側と同じ扱い。
        var unified = $"--unified={contextLines}";
        var literal = GitCompareArgs.LiteralPathspecs;
        var args = staged
            ? new[] { literal, "diff", "--cached", unified, "--", entry.Path }
            : new[] { literal, "diff", unified, "--", entry.Path };
        var result = await _runner.RunAsync(args).ConfigureAwait(false);
        return result.Success ? result.Output : result.Message;
    }

    public async Task<string?> GetConflictStageContentAsync(string path, int stage)
    {
        var result = await _runner.RunAsync("show", $":{stage}:{path}").ConfigureAwait(false);
        return result.Success ? result.Output : null;
    }

    public async Task<(string? Base, string? Ours, string? Theirs)> GetConflictSidesAsync(string path)
    {
        var baseContent = await GetConflictStageContentAsync(path, 1).ConfigureAwait(false);
        var ours = await GetConflictStageContentAsync(path, 2).ConfigureAwait(false);
        var theirs = await GetConflictStageContentAsync(path, 3).ConfigureAwait(false);
        return (baseContent, ours, theirs);
    }

    // ===== 特定リビジョンのファイル =====

    /// <summary>
    /// そのコミット時点のファイル内容（<c>git show &lt;rev&gt;:&lt;path&gt;</c>）。存在しなければ理由付きで失敗を返す
    /// （そのコミットではまだ無かった／別名だったファイルを「空ファイル」と偽らないため）。
    /// <paramref name="relativePath"/> はリポジトリルート基準・"/" 区切り。
    /// </summary>
    public async Task<GitCommandResult> GetFileAtRevisionAsync(string revision, string relativePath)
    {
        var path = Normalize(relativePath);
        var result = await _runner
            .RunAsync(GitCompareArgs.LiteralPathspecs, "show", $"{revision}:{path}")
            .ConfigureAwait(false);
        return result.Success
            ? result
            : new GitCommandResult(result.ExitCode, "",
                $"{revision} にこのファイルはありません（{path}）。\n{result.Error}".TrimEnd());
    }

    /// <summary>
    /// 作業ツリーのファイルをそのコミット時点の内容へ戻す（<c>git checkout &lt;rev&gt; -- &lt;path&gt;</c>）。
    /// インデックスにも入るので、そのままコミットすれば「戻した」というコミットになる（履歴は書き換えない）。
    /// </summary>
    public Task<GitCommandResult> RestoreFileAtRevisionAsync(string revision, string relativePath) =>
        _mutations.ExecuteAsync(
            GitCompareArgs.LiteralPathspecs, "checkout", revision, "--", Normalize(relativePath));

    private static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    public async Task<GitCommandResult> ApplyCachedPatchAsync(string patch, bool reverse)
    {
        var gitDirectory = await _runner.GetGitDirectoryAsync().ConfigureAwait(false);
        if (gitDirectory is null)
            return new GitCommandResult(-1, "", "git ディレクトリを特定できませんでした。");

        var patchPath = Path.Combine(gitDirectory, "loomo-hunk.patch");
        try
        {
            var normalized = patch.Replace("\r\n", "\n");
            if (!normalized.EndsWith('\n')) normalized += "\n";
            await File.WriteAllTextAsync(
                patchPath, normalized, new UTF8Encoding(false)).ConfigureAwait(false);
            var args = new List<string> { "apply", "--cached", "--whitespace=nowarn" };
            if (reverse) args.Add("-R");
            args.Add(patchPath);
            return await _mutations.ExecuteAsync(args.ToArray()).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(patchPath); } catch { /* 後始末の失敗は無視 */ }
        }
    }

    private static string BuildUntrackedPatch(string path, string content)
    {
        var builder = new StringBuilder();
        builder.Append("# 未追跡ファイル: ").Append(path).Append('\n');
        if (content.Length == 0) return builder.ToString().TrimEnd('\n');
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0) count--;
        builder.Append("@@ -0,0 +1,").Append(count).Append(" @@\n");
        for (var index = 0; index < count; index++)
            builder.Append('+').Append(lines[index]).Append('\n');
        return builder.ToString().TrimEnd('\n');
    }
}
