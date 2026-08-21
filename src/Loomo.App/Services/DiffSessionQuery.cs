using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

public sealed record DiffFileList(IReadOnlyList<DiffFileItem> Items, string EmptyMessage);

/// <summary>作業ツリー、コミット範囲から Diff ファイル一覧を読み込む Query。</summary>
public sealed class DiffSessionQuery
{
    private readonly GitService _git;

    public DiffSessionQuery(GitService git)
    {
        _git = git;
    }

    public async Task<DiffFileList> LoadAsync((string? From, string To)? range)
        => range is { } commitRange ? await LoadCommitRangeAsync(commitRange) : await LoadWorkingTreeAsync();

    // マルチルート：作業ツリー・コミット範囲の項目は常に「今 Git 操作の対象になっているフォルダー」
    // （_git.RootPath）基準。
    public string ToDisplayPath(string fullPath)
    {
        var root = _git.RootPath;
        if (!string.IsNullOrEmpty(root) && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return fullPath[root.Length..].TrimStart('\\', '/').Replace('\\', '/');
        return fullPath;
    }

    public static bool SameFiles(IReadOnlyList<DiffFileItem> left, IReadOnlyList<DiffFileItem> right)
    {
        if (left.Count != right.Count) return false;
        return left.Zip(right).All(pair =>
            string.Equals(pair.First.FullPath, pair.Second.FullPath, StringComparison.OrdinalIgnoreCase)
            && pair.First.DisplayPath == pair.Second.DisplayPath
            && pair.First.Badge == pair.Second.Badge && pair.First.Stats == pair.Second.Stats
            && pair.First.IsCompare == pair.Second.IsCompare
            && pair.First.IsStaged == pair.Second.IsStaged
            && pair.First.OldContent == pair.Second.OldContent && pair.First.NewContent == pair.Second.NewContent
            && Equals(pair.First.Entry, pair.Second.Entry) && Equals(pair.First.CommitFile, pair.Second.CommitFile));
    }

    private async Task<DiffFileList> LoadWorkingTreeAsync()
    {
        var status = await _git.GetStatusAsync();
        if (!status.IsRepository)
            return new DiffFileList(Array.Empty<DiffFileItem>(), "このワークスペースは git リポジトリではありません。");
        var root = _git.RootPath ?? "";
        var items = status.Staged.Select(entry => (entry, true)).Concat(status.Unstaged.Select(entry => (entry, false)))
            .Select(pair =>
            {
                var (entry, staged) = pair;
                var badge = entry.IsConflicted ? "U" : entry.IsUntracked ? "?"
                    : (staged ? entry.IndexStatus : entry.WorkStatus).ToString();
                return new DiffFileItem
                {
                    FullPath = Path.Combine(root, entry.Path), DisplayPath = entry.Path,
                    Badge = staged ? $"{badge}（staged）" : badge, Entry = entry, IsStaged = staged,
                };
            }).ToList();
        return new DiffFileList(items, "Git の変更はありません。");
    }

    private async Task<DiffFileList> LoadCommitRangeAsync((string? From, string To) range)
    {
        var root = _git.RootPath ?? "";
        var items = (await _git.GetRangeChangesAsync(range.From, range.To)).Select(change => new DiffFileItem
        {
            FullPath = Path.Combine(root, change.Path), DisplayPath = change.Path,
            Badge = change.Status.ToString(), CommitFile = change,
        }).ToList();
        return new DiffFileList(items, "この範囲に変更ファイルはありません。");
    }
}
