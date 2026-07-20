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

        var unified = $"--unified={contextLines}";
        var args = staged
            ? new[] { "diff", "--cached", unified, "--", entry.Path }
            : new[] { "diff", unified, "--", entry.Path };
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
