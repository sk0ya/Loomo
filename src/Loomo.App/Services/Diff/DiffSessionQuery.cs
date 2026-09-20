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

    /// <param name="compareBase">比較基準の解決結果。null または作業ツリー基準なら従来どおり
    /// <c>git status</c> の作業ツリー一覧。コミット範囲（<paramref name="range"/>）を表示している間は
    /// そちらが優先で、比較基準は効かない（同時に2つの「何と比べているか」は持たない）。</param>
    public async Task<DiffFileList> LoadAsync(
        (string? From, string To)? range, GitCompareResolution? compareBase = null)
    {
        if (range is { } commitRange)
            return await LoadCommitRangeAsync(commitRange);
        if (compareBase is { HasError: true })
            return new DiffFileList(Array.Empty<DiffFileItem>(), compareBase.Error!);
        if (compareBase is { BaseRef: not null })
            return await LoadCompareBaseAsync(compareBase);
        return await LoadWorkingTreeAsync();
    }

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
            && Equals(pair.First.Entry, pair.Second.Entry) && Equals(pair.First.CommitFile, pair.Second.CommitFile)
            // 基準 ref も含めて比べる。ここを見ないと「基準だけ変えたら一覧の見た目は同じ」ケースで
            // 早期 return してしまい、古い基準の差分が残る。
            && Equals(pair.First.CompareBaseFile, pair.Second.CompareBaseFile));
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

    /// <summary>
    /// 比較基準（ブランチ／分岐点）に対する変更ファイル一覧。<c>git diff --name-status &lt;base&gt;</c>
    /// の二点記法なので未コミットの編集も含み、<b>未追跡ファイルは含まない</b>。リネームは
    /// <c>R</c> の1件（旧パス付き）にまとまる。
    /// </summary>
    private async Task<DiffFileList> LoadCompareBaseAsync(GitCompareResolution resolution)
    {
        var root = _git.RootPath ?? "";
        var baseRef = resolution.BaseRef!;
        var changes = await _git.GetCompareChangesAsync(baseRef);
        // 取得そのものが失敗したら、空一覧を「変更なし」と名乗らせない（差分があるのに無いと嘘をつく）。
        if (changes.HasError)
            return new DiffFileList(Array.Empty<DiffFileItem>(), changes.Error!);
        var items = changes.Files.Select(change => new DiffFileItem
        {
            FullPath = Path.Combine(root, change.Path), DisplayPath = change.Path,
            Badge = change.Status.ToString(),
            CompareBaseFile = new GitCompareFile(baseRef, change),
        }).ToList();
        return new DiffFileList(items, $"{resolution.Label}：変更ファイルはありません。");
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
