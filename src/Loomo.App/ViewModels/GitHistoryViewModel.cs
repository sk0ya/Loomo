using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>Git コミット履歴のページング、スコープ、絞り込み、選択詳細を管理する。</summary>
public sealed partial class GitHistoryViewModel : ObservableObject
{
    private const int PageSize = 200;

    /// <summary>検索語を打っている間に git を毎打鍵で走らせないための待ち（ミリ秒）。</summary>
    private const int RequeryDebounceMs = 300;

    public const string AllAuthorsLabel = "（すべての作者）";
    private readonly GitSessionQuery _query;
    private string? _branch;
    private string? _path;
    /// <summary>パス絞り込みの対象がファイルか（<c>--follow</c> はファイル1件でしか使えない）。</summary>
    private bool _pathIsFile;
    private int _loadedCommitCount;
    private CommitLogFilter _parsedFilter = CommitLogFilter.Parse(null);

    /// <summary>作者ドロップダウンの選択肢。読み込んだページで見かけた作者を<b>足していく</b>
    /// （絞り込み中は一覧がその作者だけになるので、そのつど作り直すと選択肢が1件に痩せてしまう）。</summary>
    private readonly SortedSet<string> _knownAuthors = new(StringComparer.CurrentCultureIgnoreCase);

    /// <summary>絞り込みの再問い合わせ世代。打鍵ごとの遅延実行のうち最新だけを通す。</summary>
    private int _requeryGeneration;

    /// <summary>一覧読み込みの世代。追い越された古い読み込みの結果を捨てるための番号。</summary>
    private int _loadGeneration;

    /// <summary>まとめて絞り込みを書き換えている間（＝自分で読み直す側）だけ立てる抑止旗。</summary>
    private bool _suppressRequery;

    /// <summary>進行中の追加読み込み。同時に呼ばれたら新しく走らせず、これに相乗りする。</summary>
    private Task<bool>? _loadingMore;

    [ObservableProperty] private GitLogRow? _selectedLogRow;
    [ObservableProperty] private string _commitDetail = "";
    [ObservableProperty] private bool _isPathScoped;
    [ObservableProperty] private string _pathScopeLabel = "";
    [ObservableProperty] private bool _isLogScoped;
    [ObservableProperty] private string _logFilter = "";
    [ObservableProperty] private IReadOnlyList<string> _authorOptions = new[] { AllAuthorsLabel };
    [ObservableProperty] private string _authorSelection = AllAuthorsLabel;
    [ObservableProperty] private DateTime? _dateFrom;
    [ObservableProperty] private DateTime? _dateTo;
    [ObservableProperty] private bool _hasMoreLog;
    [ObservableProperty] private bool _isLoadingMoreLog;

    /// <summary>パス絞り込み中のリポジトリルート基準パス（"/" 区切り）。絞っていなければ null。</summary>
    public string? ScopedPath => _path;

    /// <summary>絞り込み中のパスがファイル（＝そのコミット時点の中身を引ける対象）か。</summary>
    public bool IsFileScoped => _pathIsFile && _path is { Length: > 0 };

    public ObservableCollection<GitLogRow> LogRows { get; } = new();
    public System.ComponentModel.ICollectionView LogView { get; }
    private string? EffectiveAuthor => string.IsNullOrEmpty(AuthorSelection)
        || AuthorSelection == AllAuthorsLabel ? null : AuthorSelection;
    public bool HasActiveFilters => !_parsedFilter.IsEmpty || EffectiveAuthor is not null
        || DateFrom.HasValue || DateTo.HasValue;

    public GitHistoryViewModel(GitSessionQuery query)
    {
        _query = query;
        LogView = CollectionViewSource.GetDefaultView(LogRows);
        LogView.Filter = FilterLogRow;
    }

    partial void OnLogFilterChanged(string value)
    {
        _parsedFilter = CommitLogFilter.Parse(value);
        RefreshView();
        // 打鍵のたびに git を起こさない。押し下げた条件で読み直すのは手が止まってから。
        ScheduleRequery(RequeryDebounceMs);
    }
    partial void OnAuthorSelectionChanged(string value)
    {
        RefreshView();
        ScheduleRequery(0);
    }
    partial void OnDateFromChanged(DateTime? value)
    {
        RefreshView();
        ScheduleRequery(0);
    }
    partial void OnDateToChanged(DateTime? value)
    {
        RefreshView();
        ScheduleRequery(0);
    }

    /// <summary>
    /// 絞り込みが変わったので git へ問い合わせ直す。<b>クライアント側の篩だけでは足りない</b>——
    /// 読み込み済みのページの外にある古いコミットは、何を打っても出てこないため
    /// （<see cref="CommitLogFilter.ApplyTo"/> が git の <c>--author</c>/<c>--grep</c>/<c>--since</c> へ翻訳する）。
    /// 直前の予約は世代で無効化するので、連打しても最後の1回だけが走る。
    /// </summary>
    private async void ScheduleRequery(int delayMilliseconds)
    {
        // 世代は<b>抑止中でも</b>進める。ここで進めないと、打鍵で予約済みの読み直しが
        // 「絞り込みを落として自分で読む」経路（コミットへ手繰る等）を追い越して後から走り、
        // せっかく着地した選択を消してしまう——抑止旗の目的そのものが破れる。
        var generation = ++_requeryGeneration;
        if (_suppressRequery) return;
        try
        {
            // 遅延なしでも一度は手放す。プロパティのセッターから同期的に git を起こすと、
            // まとめて書き換えている途中の半端な条件で読み直しが走る。
            if (delayMilliseconds > 0)
                await Task.Delay(delayMilliseconds);
            else
                await Task.Yield();
            if (generation != _requeryGeneration) return;
            await ReloadAsync();
        }
        catch (Exception)
        {
            // 絞り込みの読み直しが失敗しても一覧は前の内容のまま残す（例外で落とさない）。
        }
    }
    partial void OnSelectedLogRowChanged(GitLogRow? value)
    {
        if (value?.Hash is { } hash) _ = LoadDetailAsync(hash);
    }

    [RelayCommand]
    private void ClearLogFilters() => ClearFilters(requery: true);

    /// <summary>
    /// 絞り込みを全部落とす。<paramref name="requery"/> が false なら読み直しを予約しない
    /// ——呼び出し側が自分でページを読み直している最中（コミットを手繰る途中）に、
    /// 裏で並行して読み直しが走ると一覧が入れ替わって選択が迷子になるため。
    /// </summary>
    private void ClearFilters(bool requery) => Batch(requery, () =>
    {
        LogFilter = "";
        AuthorSelection = AllAuthorsLabel;
        DateFrom = null;
        DateTo = null;
    });

    [RelayCommand]
    private void ClearDateFilter() => Batch(requery: true, () =>
    {
        DateFrom = null;
        DateTo = null;
    });

    /// <summary>
    /// 複数の絞り込みをまとめて書き換える。1つずつ読み直すと<b>半端な条件の git を何本も起こす</b>
    /// （✕ で4つ落とすと3本走る）ので、書き換えの間は抑止して最後に1回だけ予約する。
    /// <paramref name="requery"/> が false なら予約もしない——呼び出し側が自分で読み直す場合。
    /// </summary>
    private void Batch(bool requery, Action assign)
    {
        _suppressRequery = true;
        try { assign(); }
        finally { _suppressRequery = false; }
        if (requery) ScheduleRequery(0);
    }

    [RelayCommand]
    private Task ClearPathScope()
    {
        ResetPathScope();
        return ReloadAsync();
    }

    /// <summary>
    /// 一覧を読み直す。<b>読み込みは重なりうる</b>——絞り込みの再問い合わせ・リポジトリ変更の通知・
    /// スコープ切替がそれぞれ独立に呼ぶので、素直に書くと2本が同じ一覧へ追記して<b>行が二重に出る</b>。
    /// 世代で番をして、追い越された古い読み込みは結果ごと捨てる。
    /// 一覧を空にするのも git から返ってきた後（先に消すと読み込みのたびに一瞬空になる）。
    /// </summary>
    public async Task ReloadAsync()
    {
        var generation = ++_loadGeneration;
        var selectedHash = SelectedLogRow?.Hash;
        var page = await _query.GetLogPageAsync(BuildQuery(0));
        if (generation != _loadGeneration) return;

        LogRows.Clear();
        _loadedCommitCount = 0;
        SelectedLogRow = ApplyPage(page, selectedHash);
        if (SelectedLogRow is null) CommitDetail = "";
        UpdateAuthorOptions();
    }

    /// <summary>次のページを足す。<b>一覧が伸びたかどうかを返す</b>——呼び直しても進まない状態
    /// （もう無い／読み直しに追い越された）を呼び出し側が見分けられないと、
    /// 「見つかるまで読み進める」側が終わらない輪に入る（<see cref="SelectLoadedOrOlderAsync"/>）。
    ///
    /// <para>既に走っている読み込みがあれば<b>それに相乗りする</b>。以前はここで即 return して
    /// いたが、そうすると一覧のスクロール（撃ちっぱなしの <c>LoadMoreAsync</c>）の最中に
    /// 「そのコミットまで手繰る」が始まったとき、戻り先が同期完了になって <c>await</c> が
    /// UI スレッドを譲らず、走っている読み込みの続きが永久に走れない＝画面ごと固まっていた。</para></summary>
    public Task<bool> LoadMoreAsync()
    {
        if (_loadingMore is { } running) return running;
        if (!HasMoreLog) return Task.FromResult(false);
        var task = LoadMoreCoreAsync();
        // 同期で終わっていたら finally が先に走って片付け済み。完了済みの Task を覚え込ませると
        // 以降ずっとそれを返して、二度と読み進まなくなる。
        if (!task.IsCompleted) _loadingMore = task;
        return task;
    }

    private async Task<bool> LoadMoreCoreAsync()
    {
        IsLoadingMoreLog = true;
        try
        {
            var generation = _loadGeneration;
            var before = LogRows.Count;
            var page = await _query.GetLogPageAsync(BuildQuery(_loadedCommitCount));
            // 読み込み中に読み直しが始まっていたら、この続きはもう別の一覧のもの
            if (generation != _loadGeneration) return false;
            ApplyPage(page, SelectedLogRow?.Hash);
            UpdateAuthorOptions();
            return LogRows.Count > before;
        }
        finally
        {
            _loadingMore = null;
            IsLoadingMoreLog = false;
        }
    }

    public Task ShowBranchAsync(GitBranchInfo branch)
    {
        _branch = branch.Name;
        IsLogScoped = true;
        _knownAuthors.Clear();
        return ReloadAsync();
    }

    public Task ShowAllBranchesAsync()
    {
        _branch = null;
        IsLogScoped = false;
        _knownAuthors.Clear();
        return ReloadAsync();
    }

    /// <summary>
    /// パス（ファイル／フォルダー）の履歴に絞る。ファイルなら <c>--follow</c> でリネームを追う
    /// ——追わないと「名前を変えた日」で履歴がぷつりと切れて、それ以前が無かったことになる。
    /// <paramref name="selectHash"/> 指定時は絞り込みを落としてから読む（その1件へ確実に着地させるため）。
    /// </summary>
    public async Task ShowPathAsync(string root, string fullPath, string? selectHash = null)
    {
        _path = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        _pathIsFile = File.Exists(fullPath);
        PathScopeLabel = _path;
        IsPathScoped = true;
        _knownAuthors.Clear();
        if (!string.IsNullOrWhiteSpace(selectHash)) ClearFilters(requery: false);
        await ReloadAsync();
        if (!string.IsNullOrWhiteSpace(selectHash)) await SelectLoadedOrOlderAsync(selectHash);
    }

    public async Task SelectCommitAsync(string hash)
    {
        _branch = null;
        IsLogScoped = false;
        ResetPathScope();
        // 絞り込みが残っていると git 側が篩ってしまい、目的のコミットまで手繰れない。
        ClearFilters(requery: false);
        await ReloadAsync();
        await SelectLoadedOrOlderAsync(hash);
    }

    public void Clear()
    {
        LogRows.Clear();
        _knownAuthors.Clear();
        UpdateAuthorOptions();
        CommitDetail = "";
        ResetPathScope();
    }

    public void ResetPathScope()
    {
        _path = null;
        _pathIsFile = false;
        IsPathScoped = false;
        PathScopeLabel = "";
    }

    /// <summary>
    /// 今の絞り込みを git の引数へ翻訳する。作者ドロップダウン・日付ピッカー・検索式の
    /// 押し下げ可能な部分（<see cref="CommitLogFilter.ApplyTo"/>）が合流する唯一の場所。
    /// </summary>
    private GitLogQuery BuildQuery(int skip)
    {
        var query = new GitLogQuery
        {
            BranchRef = _branch,
            Limit = PageSize,
            Skip = skip,
            PathFilter = _path,
            FollowRenames = _pathIsFile,
            Authors = EffectiveAuthor is { } author ? new[] { author } : Array.Empty<string>(),
            Since = ToDateOnly(DateFrom),
            Until = ToDateOnly(DateTo),
        };
        return _parsedFilter.ApplyTo(query);
    }

    private static DateOnly? ToDateOnly(DateTime? value) =>
        value is { } date ? DateOnly.FromDateTime(date) : null;

    /// <summary>読み込んだページを一覧へ足す（UI スレッド上の同期処理）。戻り値は再選択すべき行。</summary>
    private GitLogRow? ApplyPage(IReadOnlyList<GitLogRow> page, string? reselectHash)
    {
        var count = 0;
        GitLogRow? reselect = null;
        foreach (var row in page)
        {
            LogRows.Add(row);
            if (row.IsCommit) { count++; _loadedCommitCount++; }
            if (reselectHash is not null && row.Hash == reselectHash) reselect = row;
        }
        HasMoreLog = count >= PageSize;
        return reselect;
    }

    private async Task SelectLoadedOrOlderAsync(string hash)
    {
        var target = FindCommitRow(hash);
        // 進まなくなったら抜ける。HasMoreLog だけで回すと、読み込みが進まない状態
        // （読み直しに追い越された等）でそのまま回り続ける。
        while (target is null && HasMoreLog && await LoadMoreAsync())
            target = FindCommitRow(hash);
        if (target is null) return;
        // 既に手元にある行なので、読み直さずに篩だけ外して見えるようにする
        // （ここで読み直すと、この後の選択が入れ替わった一覧の上で迷子になる）。
        if (!LogView.Contains(target)) ClearFilters(requery: false);
        SelectedLogRow = target;
    }

    private GitLogRow? FindCommitRow(string hash)
    {
        var sought = hash.Trim().TrimStart('^');
        return LogRows.FirstOrDefault(row => row is { IsCommit: true, Hash: { } candidate }
            && (string.Equals(candidate, sought, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(sought, StringComparison.OrdinalIgnoreCase)));
    }

    private bool FilterLogRow(object item)
    {
        if (!HasActiveFilters) return true;
        if (item is not GitLogRow { IsCommit: true } row || !_parsedFilter.Matches(row)) return false;
        if (EffectiveAuthor is { } author && !string.Equals(row.Author, author, StringComparison.Ordinal)) return false;
        if (!DateFrom.HasValue && !DateTo.HasValue) return true;
        var day = CommitLogFilter.DayOf(row);
        if (day is null) return false;
        return (DateFrom is not { } from || string.CompareOrdinal(day, from.ToString("yyyy-MM-dd")) >= 0)
            && (DateTo is not { } to || string.CompareOrdinal(day, to.ToString("yyyy-MM-dd")) <= 0);
    }

    private void RefreshView()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        LogView.Refresh();
    }

    /// <summary>
    /// 作者の選択肢を更新する。見かけた作者を<b>足すだけ</b>で、消さない——作者で絞り込むと
    /// 一覧はその作者のコミットだけになるので、そのつど作り直すと選択肢が1件に痩せて
    /// 「他の作者へ切り替える」ができなくなる。スコープ（ブランチ／パス／リポジトリ）が
    /// 変わったときだけ <see cref="_knownAuthors"/> ごと捨てる。
    /// 顔ぶれが変わらないときは<b>同じインスタンスを返す</b>（ComboBox の選択・開閉を壊さない）。
    /// </summary>
    private void UpdateAuthorOptions()
    {
        var added = false;
        foreach (var row in LogRows)
            if (row.IsCommit && !string.IsNullOrEmpty(row.Author))
                added |= _knownAuthors.Add(row.Author!);

        if (added || AuthorOptions.Count != _knownAuthors.Count + 1)
        {
            var options = new List<string>(_knownAuthors.Count + 1) { AllAuthorsLabel };
            options.AddRange(_knownAuthors);
            AuthorOptions = options;
        }
        if (!AuthorOptions.Contains(AuthorSelection, StringComparer.Ordinal))
            AuthorSelection = AllAuthorsLabel;
    }

    private async Task LoadDetailAsync(string hash) => CommitDetail = await _query.GetCommitSummaryAsync(hash);
}
