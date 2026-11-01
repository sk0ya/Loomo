using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>リポジトリ一覧の1行（§40.8.2）。表示は印（<see cref="Chip"/>）に結び、並べ替えは読み込み済みの状態の数で。</summary>
public sealed class RepositoryOverviewRow
{
    public RepositoryOverviewRow(GitRepositoryInfo info, RepositoryChipViewModel chip, string displayPath,
        GitRepositoryFileState? state, DateTime? lastFetchUtc)
    {
        Info = info;
        Chip = chip;
        DisplayPath = displayPath;
        State = state;
        LastFetchUtc = lastFetchUtc;
    }

    public GitRepositoryInfo Info { get; }
    public RepositoryChipViewModel Chip { get; }
    public string Name => Info.Name;
    public string Root => Info.Root;
    /// <summary>ワークスペースの中の位置（フォルダーが2つ以上ならフォルダー名を前置）。</summary>
    public string DisplayPath { get; }
    public GitRepositoryFileState? State { get; }
    public DateTime? LastFetchUtc { get; }

    public string KindText => Info.Kind switch
    {
        GitRepositoryKind.LinkedWorktree => "ワークツリー",
        GitRepositoryKind.Submodule => "サブモジュール",
        _ => "",
    };

    public string LastFetchText => LastFetchUtc is { } at ? at.ToLocalTime().ToString("M/d HH:mm") : "—";
}

/// <summary>
/// ファイル一覧の「リポジトリ一覧」（§40.8.2）：全リポジトリを表で並べ、並べ替え・複数選択して fetch／pull をまとめて。
/// ファイル一覧は「集合を処理する」面（§26.10）なので、リポジトリの集合を扱う表はここに置く（Git セッションには置かない）。
/// 並べ替えの約束は §26.10 と同じ：同値は名前昇順（読み直すたびに並びが揺れない）、数は選んだ直後だけ降順から。
/// </summary>
public sealed partial class RepositoryOverviewViewModel : ObservableObject
{
    private readonly GitWorkspaceStatus _status;
    private readonly GitRepositoryChips _chips;
    private readonly IWorkspaceService _workspace;
    private int _refreshGeneration;

    public RepositoryOverviewViewModel(GitWorkspaceStatus status, GitRepositoryChips chips, IWorkspaceService workspace)
    {
        _status = status;
        _chips = chips;
        _workspace = workspace;
    }

    public ObservableCollection<RepositoryOverviewRow> Rows { get; } = new();

    /// <summary>並べ替えの列（Name／Branch／Ahead／Behind／Changes／Operation／LastFetch／Path）。</summary>
    [ObservableProperty] private string _sortKey = "Name";
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _message = "";

    /// <summary>行の Enter／ダブルクリック：そのリポジトリの根へ移る（カラムが受ける）。</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>一覧を作り直す（一覧の作成・状態の読み込みは裏で）。</summary>
    public async Task RefreshAsync()
    {
        var generation = ++_refreshGeneration;
        var built = await Task.Run(() =>
        {
            var rows = new List<RepositoryOverviewRow>();
            foreach (var repository in _status.Catalog.Repositories)
            {
                GitRepositoryFileState? state;
                try { state = _status.Load(repository.Root, fresh: false, CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { state = null; }
                rows.Add(new RepositoryOverviewRow(repository, _chips.For(repository.Root),
                    _workspace.ToDisplayPath(repository.Root), state, LastFetch(repository.Root)));
            }
            return rows;
        });
        if (generation != _refreshGeneration) return;
        Apply(Sort(built, SortKey, SortDescending));
        Message = built.Count == 0 ? "ワークスペースに Git リポジトリがありません。" : "";
    }

    /// <summary>列の見出しを押した：同じ列なら向きを反転、別の列なら数・日時は降順から、文字は昇順から。</summary>
    public void SortBy(string key)
    {
        if (string.Equals(key, SortKey, StringComparison.Ordinal))
            SortDescending = !SortDescending;
        else
        {
            SortKey = key;
            SortDescending = key is "Ahead" or "Behind" or "Changes" or "LastFetch";
        }
        Apply(Sort(Rows.ToList(), SortKey, SortDescending));
    }

    public void Open(RepositoryOverviewRow? row)
    {
        if (row is not null)
            OpenRequested?.Invoke(this, row.Root);
    }

    /// <summary>選んだリポジトリで fetch（または pull）を順に。1 つずつ——同時に走らせると認証の問いや帯域が重なる。</summary>
    public async Task RunOnAsync(IReadOnlyList<RepositoryOverviewRow> rows, bool pull)
    {
        if (rows.Count == 0 || IsBusy) return;
        IsBusy = true;
        var failed = 0;
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                Message = $"{row.Name} を{(pull ? "取り込み" : "取得")}中…（{i + 1}/{rows.Count}）";
                if (pull) await row.Chip.PullCommand.ExecuteAsync(null);
                else await row.Chip.FetchCommand.ExecuteAsync(null);
                if (row.Chip.LastError.Length > 0) failed++;
            }
            Message = failed == 0 ? $"{rows.Count} 件を{(pull ? "取り込み" : "取得")}ました。" : $"{failed} 件が失敗しました（✕ にエラー）。";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    // 同じ並びなら作り直さない（選択とスクロールを保つ）。
    private void Apply(IReadOnlyList<RepositoryOverviewRow> rows)
    {
        if (Rows.Count == rows.Count && Rows.Select(r => r.Root).SequenceEqual(rows.Select(r => r.Root), StringComparer.OrdinalIgnoreCase)
            && Rows.Zip(rows).All(p => ReferenceEquals(p.First.State, p.Second.State) && p.First.LastFetchUtc == p.Second.LastFetchUtc))
            return;
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);
    }

    /// <summary>並べ替え（純関数・テスト用に internal）。同値は名前昇順。</summary>
    internal static IReadOnlyList<RepositoryOverviewRow> Sort(IReadOnlyList<RepositoryOverviewRow> rows, string key, bool descending)
    {
        IComparable KeyOf(RepositoryOverviewRow row) => key switch
        {
            "Branch" => row.State?.Branch ?? "",
            "Ahead" => row.State?.Ahead ?? 0,
            "Behind" => row.State?.Behind ?? 0,
            "Changes" => row.State?.ChangedCount ?? 0,
            "Operation" => (int)(row.State?.Operation ?? GitRepositoryOperation.None),
            "LastFetch" => row.LastFetchUtc ?? DateTime.MinValue,
            "Path" => row.DisplayPath,
            _ => row.Name,
        };
        var ordered = descending
            ? rows.OrderByDescending(KeyOf, Comparer<IComparable>.Create(CompareKeys))
            : rows.OrderBy(KeyOf, Comparer<IComparable>.Create(CompareKeys));
        return ordered.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Root, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CompareKeys(IComparable? a, IComparable? b)
        => a is string sa && b is string sb
            ? StringComparer.OrdinalIgnoreCase.Compare(sa, sb)
            : Comparer<IComparable>.Default.Compare(a!, b!);

    /// <summary>最後に取得した時刻＝FETCH_HEAD の更新時刻（§24.92 と同じ物差し）。ワークツリーは共通ディレクトリにある。</summary>
    private static DateTime? LastFetch(string root)
    {
        try
        {
            var gitDirectory = GitRepositoryCatalog.ResolveGitDirectory(root);
            if (gitDirectory is null) return null;
            foreach (var candidate in new[] { gitDirectory, CommonOf(gitDirectory) })
            {
                if (candidate is null) continue;
                var path = Path.Combine(candidate, "FETCH_HEAD");
                if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static string? CommonOf(string gitDirectory)
    {
        var parent = Path.GetDirectoryName(gitDirectory);
        return parent is not null && Path.GetFileName(parent).Equals("worktrees", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(parent)
            : null;
    }
}
