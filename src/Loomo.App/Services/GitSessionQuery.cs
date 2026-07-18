using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

public sealed record GitSessionOverview(
    GitStatusSnapshot Status,
    IReadOnlyList<GitBranchInfo> Branches,
    IReadOnlyList<string> Remotes,
    IReadOnlyList<GitTagInfo> Tags,
    IReadOnlyList<GitSubmoduleInfo> Submodules);

/// <summary>Git セッションのコミット詳細と作業ツリーファイルを読み取る Query。</summary>
public sealed class GitSessionQuery
{
    private readonly GitService _git;

    public GitSessionQuery(GitService git) => _git = git;

    public Task<string> GetCommitSummaryAsync(string hash) => _git.GetCommitSummaryAsync(hash);

    public async Task<GitSessionOverview> LoadOverviewAsync()
    {
        var status = await _git.GetStatusAsync();
        if (!status.IsRepository)
            return new(status, Array.Empty<GitBranchInfo>(), Array.Empty<string>(),
                Array.Empty<GitTagInfo>(), Array.Empty<GitSubmoduleInfo>());
        return new(status, await _git.GetBranchesAsync(), await _git.GetRemotesAsync(),
            await _git.GetTagsAsync(), await _git.GetSubmodulesAsync());
    }

    public Task<string> GetCommitPatchAsync(string hash) => _git.GetCommitPatchAsync(hash);

    public Task<IReadOnlyList<GitLogRow>> GetLogPageAsync(
        string? branch, int take, int skip, string? path) => _git.GetLogAsync(branch, take, skip, path);

    public string? ResolveExistingChangedFile(string relativePath)
    {
        var root = _git.RootPath;
        if (string.IsNullOrEmpty(root)) return null;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        return File.Exists(fullPath) ? fullPath : null;
    }
}
