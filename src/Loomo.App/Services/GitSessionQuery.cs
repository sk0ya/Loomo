using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>Git セッションのコミット詳細と作業ツリーファイルを読み取る Query。</summary>
public sealed class GitSessionQuery
{
    private readonly GitService _git;

    public GitSessionQuery(GitService git) => _git = git;

    public Task<string> GetCommitSummaryAsync(string hash) => _git.GetCommitSummaryAsync(hash);

    public string? ResolveExistingChangedFile(string relativePath)
    {
        var root = _git.RootPath;
        if (string.IsNullOrEmpty(root)) return null;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        return File.Exists(fullPath) ? fullPath : null;
    }
}
