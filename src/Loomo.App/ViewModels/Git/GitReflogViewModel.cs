using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Git;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 操作ログ（reflog）の1行。記録そのもの（<see cref="Entry"/>）に、一覧で読むための言い換え
/// （日付の見出し・時刻・種類の名前）と「どの ref からも辿れない」印を添える。
/// </summary>
public sealed class GitReflogRow
{
    public GitReflogRow(GitReflogEntry entry, bool isLost, DateTimeOffset now)
    {
        Entry = entry;
        IsLost = isLost;
        var local = entry.Time?.ToLocalTime();
        DayLabel = local is { } day ? GitReflogTimeFormat.Day(day, now) : "日時不明";
        TimeLabel = local?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        AbsoluteTime = local?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
        RelativeTime = local is { } time ? GitReflogTimeFormat.Relative(time, now) : "";
    }

    public GitReflogEntry Entry { get; }

    /// <summary>このコミットはどのブランチ・タグ・リモート追跡ブランチ・HEAD からも辿れない
    /// （放っておくと gc で消える＝取り戻すならいま）。</summary>
    public bool IsLost { get; }

    /// <summary>その ref のいまの位置（最新の記録）。</summary>
    public bool IsCurrent => Entry.Index == 0;

    /// <summary>日付の見出し（「今日」「昨日」「9月12日（金）」）。一覧はこれで区切る。</summary>
    public string DayLabel { get; }

    public string TimeLabel { get; }
    public string AbsoluteTime { get; }
    public string RelativeTime { get; }

    public string KindLabel => GitReflogTimeFormat.KindLabel(Entry.Kind);
    public GitReflogKind Kind => Entry.Kind;
    public string Description => Entry.Description;
    public string ShortHash => Entry.ShortHash;
    public string Selector => Entry.Selector;

    /// <summary>行のツールチップ：言い換える前の git の記録も添える（言い換えを疑えるように）。</summary>
    public string ToolTipText =>
        $"{Description}\n{AbsoluteTime}（{RelativeTime}）  {Selector}  {ShortHash}\ngit: {Entry.Message}"
        + (IsLost ? "\n\nこのコミットはどのブランチからも辿れません。ブランチを作れば取り戻せます。" : "");

    /// <summary>コミット一覧と同じ操作（ブランチ作成・チェックアウト・リセット）へ渡すための形。</summary>
    public GitLogRow ToLogRow() => new(
        "", Entry.Hash, Entry.ShortHash, Entry.Author, AbsoluteTime, null, Entry.Subject);
}

/// <summary>操作ログの時刻・種類の言い換え（純ロジック）。</summary>
public static class GitReflogTimeFormat
{
    private static readonly string[] Weekdays = { "日", "月", "火", "水", "木", "金", "土" };

    public static string Day(DateTimeOffset time, DateTimeOffset now)
    {
        var date = time.Date;
        var today = now.ToOffset(time.Offset).Date;
        if (date == today) return "今日";
        if (date == today.AddDays(-1)) return "昨日";
        var weekday = Weekdays[(int)date.DayOfWeek];
        return date.Year == today.Year
            ? $"{date.Month}月{date.Day}日（{weekday}）"
            : $"{date.Year}年{date.Month}月{date.Day}日（{weekday}）";
    }

    public static string Relative(DateTimeOffset time, DateTimeOffset now)
    {
        var span = now - time;
        if (span < TimeSpan.FromMinutes(1)) return "たった今";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes}分前";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours}時間前";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays}日前";
        if (span < TimeSpan.FromDays(365)) return $"{(int)(span.TotalDays / 30)}か月前";
        return $"{(int)(span.TotalDays / 365)}年前";
    }

    public static string KindLabel(GitReflogKind kind) => kind switch
    {
        GitReflogKind.Commit => "コミット",
        GitReflogKind.Amend => "修正",
        GitReflogKind.Merge => "マージ",
        GitReflogKind.Checkout => "切替",
        GitReflogKind.Reset => "リセット",
        GitReflogKind.Rebase => "リベース",
        GitReflogKind.Pull => "プル",
        GitReflogKind.CherryPick => "ピック",
        GitReflogKind.Revert => "リバート",
        GitReflogKind.Branch => "ブランチ",
        GitReflogKind.Clone => "クローン",
        GitReflogKind.Sync => "同期",
        GitReflogKind.Stash => "スタッシュ",
        _ => "その他",
    };
}

/// <summary>
/// Git ペインの「操作ログ」面。reflog を「いつ・何をして・どこへ動いたか」の一覧にし、
/// 選んだ記録のコミットの中身を右に出す。
///
/// <para>この面の主な用途は<b>やり直しの取り消し</b>——リセットやリベースで消えたように見えるコミットを
/// 探して取り戻すこと。なので (1) どの ref からも辿れない行に印を付け（<see cref="GitReflogRow.IsLost"/>）、
/// (2) 印の付いた行だけに絞れ（<see cref="LostOnly"/>）、(3) 「この操作の前後で何が変わったか」を
/// 差分で見られるようにしてある。取り戻す操作そのもの（ブランチ作成・リセット）はコミット一覧と同じ
/// 経路を通す（ビューが <see cref="GitReflogRow.ToLogRow"/> で渡す）。</para>
/// </summary>
public sealed partial class GitReflogViewModel : ObservableObject
{
    public const int PageSize = 200;
    public const string HeadRef = "HEAD";

    private readonly GitService _git;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>一覧読み込みの世代。追い越された古い読み込み（ref の切替・連続した更新）を捨てる。</summary>
    private int _loadGeneration;
    private int _detailGeneration;
    private bool _loaded;

    public GitReflogViewModel(GitService git, Func<DateTimeOffset>? clock = null)
    {
        _git = git;
        _clock = clock ?? (() => DateTimeOffset.Now);
        RowsView = new ListCollectionView(Rows) { Filter = Accepts };
        RowsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GitReflogRow.DayLabel)));
    }

    public ObservableCollection<GitReflogRow> Rows { get; } = new();

    /// <summary>絞り込みと日付の区切りを掛けた一覧（ビューはこれを見る）。</summary>
    public ListCollectionView RowsView { get; }

    /// <summary>見られる ref（先頭が HEAD＝すべての移動、続いてローカルブランチ）。</summary>
    [ObservableProperty] private IReadOnlyList<string> _refOptions = new[] { HeadRef };

    [ObservableProperty] private string _selectedRef = HeadRef;

    /// <summary>説明・件名・ハッシュ・ブランチ名に効く語（空白区切りで AND）。</summary>
    [ObservableProperty] private string _filter = "";

    /// <summary>どの ref からも辿れない行だけを見る。</summary>
    [ObservableProperty] private bool _lostOnly;

    [ObservableProperty] private GitReflogRow? _selectedRow;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private string? _error;

    /// <summary>選んだ記録のコミットのコメントと素性（<see cref="CommitSummary.Header"/>）。</summary>
    [ObservableProperty] private string _detailMessage = "";
    [ObservableProperty] private IReadOnlyList<CommitFileStat> _detailFiles = Array.Empty<CommitFileStat>();
    [ObservableProperty] private string _detailFileSummary = "";

    /// <summary>読み込んだ範囲の中で、どの ref からも辿れない行の数（絞り込みボタンに出す）。</summary>
    [ObservableProperty] private int _lostCount;

    /// <summary>絞り込み後に見えている件数／読み込んだ件数。</summary>
    public string CountLabel
    {
        get
        {
            var visible = RowsView.Count;
            var more = HasMore ? "+" : "";
            return visible == Rows.Count ? $"{Rows.Count}{more} 件" : $"{visible} / {Rows.Count}{more} 件";
        }
    }

    /// <summary>一覧が空のときに出す一文（読み込み中・失敗・絞り込みで0件・そもそも記録が無い）。</summary>
    public string EmptyMessage =>
        Error is { Length: > 0 } error ? $"操作ログを読めませんでした: {error}"
        : IsLoading && Rows.Count == 0 ? "読み込んでいます…"
        : Rows.Count == 0 ? $"{SelectedRef} の操作ログはまだありません。コミット・切り替え・リセットなどをするとここに残ります。"
        : RowsView.Count == 0 ? "絞り込みに一致する記録はありません。"
        : "";

    public bool IsEmpty => EmptyMessage.Length > 0;

    public bool HasSelection => SelectedRow is not null;

    /// <summary>選んだ記録が「前の状態」を持つ（＝この操作で変わった内容を差分で見られる）か。</summary>
    public bool CanShowOperationDiff => SelectedRow?.Entry.MovedCommit == true;

    /// <summary>選んだ記録の見出し（「HEAD@{3} · 今日 14:02（2時間前）」）。</summary>
    public string SelectionHeader => SelectedRow is { } row
        ? $"{row.Selector}  ·  {row.AbsoluteTime}（{row.RelativeTime}）"
        : "";

    /// <summary>選んだ記録の移動（「a1b2c3d → e4f5a6b」）。前が分からなければ移動先だけ。</summary>
    public string SelectionMove => SelectedRow?.Entry is { } entry
        ? entry.PreviousHash is { } previous
            ? entry.MovedCommit ? $"{previous[..Math.Min(7, previous.Length)]} → {entry.ShortHash}" : $"{entry.ShortHash}（移動なし）"
            : entry.ShortHash
        : "";

    /// <summary>ブランチ一覧の更新に合わせて選べる ref を差し替える。見ていた ref が消えたら HEAD へ戻す。</summary>
    public void SetBranches(IEnumerable<string> localBranches)
    {
        var options = new List<string> { HeadRef };
        options.AddRange(localBranches.Where(name => name != HeadRef).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        if (!options.SequenceEqual(RefOptions))
            RefOptions = options;
        if (!RefOptions.Contains(SelectedRef))
            SelectedRef = HeadRef;
    }

    /// <summary>操作ログ面が初めて見えたとき（以降はリポジトリの変更通知で <see cref="ReloadAsync"/>）。</summary>
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : ReloadAsync();

    /// <summary>隠れている間にリポジトリが変わった（一覧はそのまま残し、次に見えたときに読み直す）。</summary>
    public void MarkStale() => _loaded = false;

    /// <summary>リポジトリから外れたとき（空にして、次に見えたときに読み直す）。</summary>
    public void Clear()
    {
        _loadGeneration++;
        _loaded = false;
        Rows.Clear();
        SelectedRow = null;
        HasMore = false;
        Error = null;
        LostCount = 0;
        NotifyListChanged();
    }

    partial void OnSelectedRefChanged(string value)
    {
        // ref を替えたら頭から読み直す（別の ref の記録とは番号も意味も繋がらない）。
        if (_loaded) _ = LoadAsync(resetPaging: true);
    }

    partial void OnFilterChanged(string value) => RefreshView();
    partial void OnLostOnlyChanged(bool value) => RefreshView();
    partial void OnHasMoreChanged(bool value) => OnPropertyChanged(nameof(CountLabel));
    partial void OnIsLoadingChanged(bool value) => NotifyListChanged();
    partial void OnErrorChanged(string? value) => NotifyListChanged();

    partial void OnSelectedRowChanged(GitReflogRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanShowOperationDiff));
        OnPropertyChanged(nameof(SelectionHeader));
        OnPropertyChanged(nameof(SelectionMove));
        _ = LoadDetailAsync(value);
    }

    /// <summary>
    /// 読み直す。<b>読み込んだ件数は保つ</b>（さらに読み込んで遡っていたのに、操作1回で先頭200件へ
    /// 縮むと、見ていた行が一覧から消える）。選択は記録の中身で引き当て直す——番号は新しい操作が
    /// 1件増えるたびに1つずれるので、番号では追えない。
    /// </summary>
    [RelayCommand]
    public Task ReloadAsync() => LoadAsync(resetPaging: false);

    private async Task LoadAsync(bool resetPaging)
    {
        _loaded = true;
        var generation = ++_loadGeneration;
        var refName = SelectedRef;
        var take = resetPaging ? PageSize : Math.Max(PageSize, Rows.Count);
        var keep = resetPaging ? null : SelectedRow?.Entry;

        IsLoading = true;
        try
        {
            var page = await _git.GetReflogAsync(refName, 0, take);
            var lost = await _git.GetUnreachableCommitsAsync(page.Entries.Select(e => e.Hash));
            if (generation != _loadGeneration) return;

            var now = _clock();
            Rows.Clear();
            foreach (var entry in page.Entries)
                Rows.Add(new GitReflogRow(entry, lost.Contains(entry.Hash), now));
            HasMore = page.HasMore;
            Error = page.Error;
            LostCount = Rows.Count(row => row.IsLost);
            SelectedRow = keep is null ? null : Rows.FirstOrDefault(row => SameRecord(row.Entry, keep));
            NotifyListChanged();
        }
        finally
        {
            if (generation == _loadGeneration) IsLoading = false;
        }
    }

    /// <summary>同じ記録か（番号ではなく、移動先・メッセージ・時刻で見る）。</summary>
    private static bool SameRecord(GitReflogEntry a, GitReflogEntry b) =>
        a.RefName == b.RefName && a.Hash == b.Hash && a.Message == b.Message && a.Time == b.Time;

    /// <summary>古い記録を続けて読む（一覧の末尾の「さらに読み込む」）。</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!HasMore || IsLoading) return;
        var generation = ++_loadGeneration;
        var refName = SelectedRef;
        var skip = Rows.Count;

        IsLoading = true;
        try
        {
            var page = await _git.GetReflogAsync(refName, skip, PageSize);
            var lost = await _git.GetUnreachableCommitsAsync(page.Entries.Select(e => e.Hash));
            if (generation != _loadGeneration) return;

            // 読み込んでいた最後の行は、ここで初めて「操作の前」が分かる（次の記録＝今回の先頭）。
            if (Rows.Count > 0 && page.Entries.Count > 0 && Rows[^1].Entry.PreviousHash is null)
            {
                var last = Rows[^1];
                var filled = new GitReflogRow(last.Entry with { PreviousHash = page.Entries[0].Hash }, last.IsLost, _clock());
                var wasSelected = ReferenceEquals(SelectedRow, last);
                Rows[^1] = filled;
                if (wasSelected) SelectedRow = filled;
            }

            var now = _clock();
            foreach (var entry in page.Entries)
                Rows.Add(new GitReflogRow(entry, lost.Contains(entry.Hash), now));
            HasMore = page.HasMore;
            Error = page.Error;
            LostCount = Rows.Count(row => row.IsLost);
            NotifyListChanged();
        }
        finally
        {
            if (generation == _loadGeneration) IsLoading = false;
        }
    }

    [RelayCommand]
    private void ClearFilter()
    {
        Filter = "";
        LostOnly = false;
    }

    private async Task LoadDetailAsync(GitReflogRow? row)
    {
        var generation = ++_detailGeneration;
        if (row is null)
        {
            DetailMessage = "";
            DetailFiles = Array.Empty<CommitFileStat>();
            DetailFileSummary = "";
            return;
        }

        var text = await _git.GetCommitSummaryAsync(row.Entry.Hash);
        if (generation != _detailGeneration) return;
        var summary = CommitSummary.Parse(text);
        DetailMessage = summary.Header;
        DetailFiles = summary.Files;
        var added = summary.Files.Sum(file => file.Added ?? 0);
        var deleted = summary.Files.Sum(file => file.Deleted ?? 0);
        DetailFileSummary = summary.Files.Count == 0 ? "変更なし" : $"{summary.Files.Count} ファイル +{added} -{deleted}";
    }

    private void RefreshView()
    {
        RowsView.Refresh();
        NotifyListChanged();
    }

    private void NotifyListChanged()
    {
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool Accepts(object item) => item is GitReflogRow row && Matches(row, Filter, LostOnly);

    /// <summary>絞り込みの判定（純ロジック）。語は空白区切りで AND、大文字小文字は区別しない。</summary>
    public static bool Matches(GitReflogRow row, string? filter, bool lostOnly)
    {
        if (lostOnly && !row.IsLost) return false;
        if (string.IsNullOrWhiteSpace(filter)) return true;
        foreach (var term in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hit = Contains(row.Description, term) || Contains(row.Entry.Subject, term)
                || Contains(row.Entry.Message, term) || Contains(row.KindLabel, term)
                || row.Entry.Hash.StartsWith(term, StringComparison.OrdinalIgnoreCase)
                || Contains(row.Entry.Author, term);
            if (!hit) return false;
        }
        return true;

        static bool Contains(string? text, string term) =>
            text?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true;
    }
}
