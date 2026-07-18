using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>コミットログ、コミット内容、コミット範囲の差分を照会する。</summary>
public sealed class GitHistoryService
{
    private readonly GitCommandRunner _runner;

    public GitHistoryService(GitCommandRunner runner) => _runner = runner;

    public async Task<IReadOnlyList<GitLogRow>> GetLogAsync(
        string? branchRef = null, int limit = 300, int skip = 0, string? pathFilter = null)
    {
        var revArg = string.IsNullOrWhiteSpace(branchRef) ? "--all" : branchRef;
        var args = new List<string> { "log", "--graph", revArg, $"-n{limit}" };
        if (skip > 0)
            args.Add($"--skip={skip}");
        args.Add("--date=format:%Y-%m-%d %H:%M");
        args.Add($"--pretty=format:{GitLogParser.PrettyFormat}");
        if (!string.IsNullOrWhiteSpace(pathFilter))
        {
            args.Add("--");
            args.Add(pathFilter);
        }

        var result = await _runner.RunAsync(args.ToArray()).ConfigureAwait(false);
        return result.Success ? GitLogParser.Parse(result.Output) : Array.Empty<GitLogRow>();
    }

    public async Task<string> GetCommitSummaryAsync(string hash)
    {
        var result = await _runner.RunAsync("show", "--stat", "--format=fuller", hash)
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
        if (!result.Success)
            return Array.Empty<GitCommitFileChange>();

        var changes = new List<GitCommitFileChange>();
        foreach (var line in result.Output.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (value.Length == 0) continue;
            var parts = value.Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0) continue;
            var (path, originalPath) = parts.Length >= 3
                ? (parts[2], parts[1])
                : (parts[1], (string?)null);
            changes.Add(new GitCommitFileChange(parts[0][0], path, originalPath));
        }
        return changes;
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
