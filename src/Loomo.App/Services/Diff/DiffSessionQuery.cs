using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <param name="IsCanceled">人が比較を中止した（<see cref="EmptyMessage"/> はその旨）。失敗とは扱いが違う——
/// 受け手は中止された比較を自動の読み直しで<b>また走らせない</b>よう、表示を作業ツリーへ戻す。</param>
public sealed record DiffFileList(IReadOnlyList<DiffFileItem> Items, string EmptyMessage, bool IsCanceled = false)
{
    public static DiffFileList Canceled(string message) => new(Array.Empty<DiffFileItem>(), message, IsCanceled: true);
}

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
        if (compareBase is { IsCanceled: true })
            return DiffFileList.Canceled(compareBase.Error!);
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
            && pair.First.Stage == pair.Second.Stage
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
        // 1ファイル1項目。ステージ済み／未ステージで項目を分けると、ステージした行が見ていた差分から消え、
        // 別の項目へ移ってしまう——差分は HEAD↔作業ツリーの1枚にして、進み具合は印で見せる。
        var staged = status.Staged.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var unstaged = status.Unstaged.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var items = status.Unstaged.Concat(status.Staged.Where(entry => !unstaged.Contains(entry.Path)))
            .Select(entry =>
            {
                var stage = entry.IsConflicted || entry.IsUntracked ? DiffStageState.None
                    : !staged.Contains(entry.Path) ? DiffStageState.None
                    : unstaged.Contains(entry.Path) ? DiffStageState.Partial
                    : DiffStageState.All;
                return new DiffFileItem
                {
                    FullPath = Path.Combine(root, entry.Path), DisplayPath = entry.Path,
                    Badge = Badge(entry, stage), Entry = entry, Stage = stage,
                };
            })
            .OrderBy(item => item.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DiffFileList(items, "Git の変更はありません。");
    }

    /// <summary>一覧の印：変更の種類（作業ツリー側を優先し、無ければインデックス側）と、ステージの進み具合。</summary>
    private static string Badge(GitChangeEntry entry, DiffStageState stage)
    {
        if (entry.IsConflicted) return "U";
        if (entry.IsUntracked) return "?";
        var kind = entry.WorkStatus is ' ' or '.' ? entry.IndexStatus : entry.WorkStatus;
        if (entry.IndexStatus == 'A') kind = 'A';   // 新規追加をステージ済みなら、作業ツリーで直していても「追加」
        return stage switch
        {
            DiffStageState.All => $"{kind}（ステージ済み）",
            DiffStageState.Partial => $"{kind}（一部ステージ）",
            _ => kind.ToString(),
        };
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
        if (changes.IsCanceled)
            return DiffFileList.Canceled(changes.Error!);
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
        var changes = await _git.GetRangeChangesAsync(range.From, range.To);
        // 失敗（時間切れ・壊れた ref）を「この範囲に変更ファイルはありません」と名乗らせない。
        if (changes.IsCanceled)
            return DiffFileList.Canceled(changes.Error!);
        if (changes.HasError)
            return new DiffFileList(Array.Empty<DiffFileItem>(), changes.Error!);
        var items = changes.Files.Select(change => new DiffFileItem
        {
            FullPath = Path.Combine(root, change.Path), DisplayPath = change.Path,
            Badge = change.Status.ToString(), CommitFile = change,
        }).ToList();
        return new DiffFileList(items, "この範囲に変更ファイルはありません。");
    }
}
