using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <param name="IsCanceled">人が比較を中止した（<see cref="EmptyMessage"/> はその旨）。失敗とは扱いが違う——
/// 受け手は中止された比較を自動の読み直しで<b>また走らせない</b>よう、表示を作業ツリーへ戻す。</param>
public sealed record DiffFileList(IReadOnlyList<DiffFileItem> Items, string EmptyMessage, bool IsCanceled = false)
{
    public static DiffFileList Canceled(string message) => new(Array.Empty<DiffFileItem>(), message, IsCanceled: true);
}

/// <summary>差分の項目を、その項目のリポジトリの窓口へ回す（§40.8.5）。</summary>
public static class DiffRepositoryRouting
{
    /// <summary>項目のリポジトリが「選んでいるリポジトリ」ならその窓口（照会のキャッシュと変更の通知を Git パネルと
    /// 共有する）、別のリポジトリならそのリポジトリに固定した窓口（§40.5）。</summary>
    public static GitService ForItem(this GitService git, DiffFileItem? item)
        => git.ForRepository(item?.RepositoryRoot);

    /// <summary><see cref="ForItem"/> のリポジトリ版。<paramref name="root"/> が null なら選んでいるリポジトリの窓口。</summary>
    public static GitService ForRepository(this GitService git, string? root)
    {
        if (root is null)
            return git;
        return string.Equals(SelectedRepository(git), root, StringComparison.OrdinalIgnoreCase) ? git : git.For(root);
    }

    /// <summary>いま選んでいるリポジトリのルート（ワークスペースのフォルダーがリポジトリの中なら、そのリポジトリのルート）。</summary>
    public static string? SelectedRepository(GitService git)
        => git.RootPath is { } current ? git.Repositories.KnownRepositoryFor(current)?.Root ?? current : null;
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
    /// <param name="rangeRepository">コミット範囲のリポジトリ（開いたときに固定したもの）。Git パネルで対象を
    /// 切り替えても、範囲はこのリポジトリで引き続ける——ハッシュは別のリポジトリでは解決できない。
    /// null なら選んでいるリポジトリ。</param>
    public async Task<DiffFileList> LoadAsync(
        (string? From, string To)? range, GitCompareResolution? compareBase = null, string? rangeRepository = null)
    {
        if (range is { } commitRange)
            return await LoadCommitRangeAsync(commitRange, rangeRepository);
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
    public string ToDisplayPath(string fullPath, string? repository = null)
    {
        var root = repository ?? _git.RootPath;
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
        // リポジトリが2つ以上の部屋では、全リポジトリの作業ツリーの変更を1つの一覧に（§40.8.5）。以前は Git パネルで
        // 選んでいるリポジトリの分しか出ず、もう一方で直したファイルは差分ペインに出なかった。
        var repositories = await Task.Run(() => _git.Repositories.Repositories);
        if (repositories.Count > 1)
            return await LoadAllWorkingTreesAsync(repositories);
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

    /// <summary>全リポジトリの作業ツリーの変更。表示はリポジトリ名を前に付け（<c>api/src/x.ts</c>）、並びはリポジトリの順→パス。</summary>
    private async Task<DiffFileList> LoadAllWorkingTreesAsync(IReadOnlyList<GitRepositoryInfo> repositories)
    {
        var items = new List<DiffFileItem>();
        foreach (var repository in repositories)
        {
            var probe = new DiffFileItem { FullPath = repository.Root, DisplayPath = "", Badge = "", RepositoryRoot = repository.Root };
            var status = await _git.ForItem(probe).GetStatusAsync();
            if (!status.IsRepository) continue;
            items.AddRange(BuildWorkingTreeItems(status, repository.Root, repository.Name + "/")
                .Select(item => WithRepository(item, repository.Root)));
        }
        return new DiffFileList(items, "Git の変更はありません。");
    }

    private static DiffFileItem WithRepository(DiffFileItem item, string root) => new()
    {
        RepositoryRoot = root, FullPath = item.FullPath, DisplayPath = item.DisplayPath,
        Badge = item.Badge, Entry = item.Entry, Stage = item.Stage,
    };

    private static List<DiffFileItem> BuildWorkingTreeItems(GitStatusSnapshot status, string root, string displayPrefix)
    {
        var staged = status.Staged.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var unstaged = status.Unstaged.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        return status.Unstaged.Concat(status.Staged.Where(entry => !unstaged.Contains(entry.Path)))
            .Select(entry =>
            {
                var stage = entry.IsConflicted || entry.IsUntracked ? DiffStageState.None
                    : !staged.Contains(entry.Path) ? DiffStageState.None
                    : unstaged.Contains(entry.Path) ? DiffStageState.Partial
                    : DiffStageState.All;
                return new DiffFileItem
                {
                    FullPath = Path.Combine(root, entry.Path), DisplayPath = displayPrefix + entry.Path,
                    Badge = Badge(entry, stage), Entry = entry, Stage = stage,
                };
            })
            .OrderBy(item => item.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private async Task<DiffFileList> LoadCommitRangeAsync((string? From, string To) range, string? repository)
    {
        var root = repository ?? _git.RootPath ?? "";
        var changes = await _git.ForRepository(repository).GetRangeChangesAsync(range.From, range.To);
        // 失敗（時間切れ・壊れた ref）を「この範囲に変更ファイルはありません」と名乗らせない。
        if (changes.IsCanceled)
            return DiffFileList.Canceled(changes.Error!);
        if (changes.HasError)
            return new DiffFileList(Array.Empty<DiffFileItem>(), changes.Error!);
        var items = changes.Files.Select(change => new DiffFileItem
        {
            FullPath = Path.Combine(root, change.Path), DisplayPath = change.Path,
            Badge = change.Status.ToString(), CommitFile = change,
            // 項目からもそのリポジトリの窓口へ回す（本文・打ち消し・監視が ForItem で同じリポジトリを引く）。
            RepositoryRoot = repository,
        }).ToList();
        return new DiffFileList(items, "この範囲に変更ファイルはありません。");
    }
}
