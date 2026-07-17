using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// Git セッションペインの ViewModel。コミットグラフ（git log --graph）・ブランチ一覧と、
/// rebase / merge / cherry-pick / reset などサイドバーに収まらない操作を担う。
/// 名前入力（新規ブランチ等）や破壊的操作の確認はビュー側ダイアログで行い、ここは git 操作に徹する。
/// </summary>
public sealed partial class GitSessionViewModel : ObservableObject
{
    private readonly GitService _git;
    private readonly IEditorService _editor;
    private readonly DiffSessionViewModel _diff;
    private bool _loaded;
    private GitStatusSnapshot _status = new();
    private string? _lastWorkspaceRoot;

    /// <summary>直近に読み込んだブランチ一覧（絞り込みの元・上流の参照元）。</summary>
    private IReadOnlyList<GitBranchInfo> _allBranches = Array.Empty<GitBranchInfo>();

    [ObservableProperty] private bool _isRepository = true;
    [ObservableProperty] private string _branchLabel = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;

    /// <summary>選択中のコミット行。変更で詳細（show --stat）を読み込む。</summary>
    [ObservableProperty] private GitLogRow? _selectedLogRow;
    [ObservableProperty] private string _commitDetail = "";

    /// <summary>rebase / merge / cherry-pick が進行中か（続行・中止バナーの表示）。</summary>
    [ObservableProperty] private bool _operationInProgress;
    [ObservableProperty] private string _operationLabel = "";
    /// <summary>進行中操作が「スキップ」を持つか（rebase / cherry-pick のみ）。</summary>
    [ObservableProperty] private bool _operationCanSkip;

    /// <summary>ブランチ一覧のツリー（ローカル／リモートの見出し、その中を "/" でフォルダ化）。</summary>
    [ObservableProperty] private IReadOnlyList<BranchTreeNode> _branchTree = Array.Empty<BranchTreeNode>();

    /// <summary>
    /// ブランチ切替ポップアップ用の絞り込み語。<see cref="BranchTree"/> 側（Git ペインのブランチ一覧）は
    /// 絞り込まない——同じ VM を見ているが用途が違う（あちらはコミットグラフの表示範囲を選ぶ一覧）。
    /// </summary>
    [ObservableProperty] private string _branchFilter = "";

    /// <summary>絞り込み後のブランチ一覧。空語なら <see cref="BranchTree"/> と同一インスタンス。</summary>
    [ObservableProperty] private IReadOnlyList<BranchTreeNode> _filteredBranchTree = Array.Empty<BranchTreeNode>();

    /// <summary>現在ブランチの上流との差。ポップアップの同期帯がプル／プッシュの件数として出す。</summary>
    [ObservableProperty] private int _ahead;
    [ObservableProperty] private int _behind;

    /// <summary>
    /// 同期の相手になるリモート名（現在ブランチの上流があればその、無ければ最初のリモート）。
    /// リモートが1つも無ければ空＝フェッチ／プル／プッシュは無効。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemote))]
    private string _remoteLabel = "";

    /// <summary>現在ブランチの上流（例: origin/main）。未設定なら空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncTargetLabel))]
    private string _upstreamLabel = "";

    public bool HasRemote => RemoteLabel.Length > 0;

    /// <summary>同期帯の副題：プル／プッシュが実際に相手にする ref。上流未設定はそう明示する
    /// （PushAsync が -u origin HEAD で作りに行くため、押せないわけではない）。</summary>
    public string SyncTargetLabel => UpstreamLabel.Length > 0 ? UpstreamLabel : "上流未設定";

    /// <summary>タグ一覧（作成日の新しい順、フラット表示）。</summary>
    [ObservableProperty] private IReadOnlyList<GitTagInfo> _tags = Array.Empty<GitTagInfo>();

    /// <summary>サブモジュール一覧（0件ならビュー側でセクションごと隠す）。</summary>
    [ObservableProperty] private IReadOnlyList<GitSubmoduleInfo> _submodules = Array.Empty<GitSubmoduleInfo>();

    /// <summary>
    /// コミットグラフに表示するブランチ（ref）。null は全ブランチ（--all）。
    /// ブランチ一覧のダブルクリックで切り替わり、ヘッダーのチェックアウト（作業ブランチの変更）とは独立。
    /// </summary>
    private string? _logBranch;

    /// <summary>
    /// コミットグラフを絞る対象パス（リポジトリルート相対・ファイル／フォルダ）。null は絞り込みなし。
    /// FolderTree／エディタの「履歴を表示」で設定され、ブランチ範囲の切替とは独立に効く（両方指定も可）。
    /// </summary>
    private string? _logPath;

    /// <summary>特定パスの履歴に絞っているか（履歴スコープ帯の表示判定）。</summary>
    [ObservableProperty] private bool _isPathScoped;

    /// <summary>絞り込み中のパス（履歴スコープ帯に表示する。リポジトリルート相対）。</summary>
    [ObservableProperty] private string _pathScopeLabel = "";

    /// <summary>特定ブランチに絞っているか（「すべてのブランチを表示」リンクの表示判定）。</summary>
    [ObservableProperty] private bool _isLogScoped;

    /// <summary>コミット一覧の絞り込み式（<c>author:</c>／<c>msg:</c>／<c>hash:</c>／<c>ref:</c>／<c>date:</c> の
    /// トークン構文、接頭辞なしは全項目部分一致、複数語は AND）。空なら全件。git を再実行せず、
    /// 読み込み済みの一覧をクライアント側で <see cref="CommitLogFilter"/> により判定する。</summary>
    [ObservableProperty] private string _logFilter = "";

    /// <summary><see cref="LogFilter"/> を解析した述語（テキスト変更のたびに作り直す）。</summary>
    private CommitLogFilter _parsedFilter = CommitLogFilter.Parse(null);

    /// <summary>作者ドロップダウンの「すべて」を表す特別値（実在の作者名と衝突しないよう括弧付き）。</summary>
    public const string AllAuthorsLabel = "（すべての作者）";

    /// <summary>作者ドロップダウンの選択肢（先頭が <see cref="AllAuthorsLabel"/>、以降は読み込み済みログの作者）。</summary>
    [ObservableProperty] private IReadOnlyList<string> _authorOptions = new[] { AllAuthorsLabel };

    /// <summary>作者ドロップダウンの選択値。<see cref="AllAuthorsLabel"/>（既定）なら作者で絞らない。</summary>
    [ObservableProperty] private string _authorSelection = AllAuthorsLabel;

    /// <summary>期間フィルタの開始日（含む）。null なら下限なし。</summary>
    [ObservableProperty] private DateTime? _dateFrom;

    /// <summary>期間フィルタの終了日（含む）。null なら上限なし。</summary>
    [ObservableProperty] private DateTime? _dateTo;

    /// <summary>実効の作者フィルタ（「すべて」・空は null＝絞らない）。</summary>
    private string? EffectiveAuthor =>
        string.IsNullOrEmpty(AuthorSelection) || AuthorSelection == AllAuthorsLabel ? null : AuthorSelection;

    /// <summary>何らかの絞り込みが有効か（クリアボタンの表示・「全件」判定に使う）。</summary>
    public bool HasActiveFilters =>
        !_parsedFilter.IsEmpty || EffectiveAuthor is not null || DateFrom.HasValue || DateTo.HasValue;

    /// <summary>コミット一覧の1ページ分の読み込み件数（末尾スクロールでこの単位ずつ追加読み込みする）。</summary>
    private const int LogPageSize = 200;

    /// <summary>現在読み込み済みのコミット件数。次ページの <c>--skip</c> に使う（グラフ継続行は数えない）。</summary>
    private int _loadedCommitCount;

    /// <summary>まだ読み込んでいない古いコミットがあるか（末尾スクロールで追加読み込みできるか）。</summary>
    [ObservableProperty] private bool _hasMoreLog;

    /// <summary>追加読み込み中か（多重発火の抑止・読み込み中表示に使う）。</summary>
    [ObservableProperty] private bool _isLoadingMoreLog;

    public ObservableCollection<GitLogRow> LogRows { get; } = new();

    /// <summary>ビューにバインドするフィルタ済みコミット一覧。<see cref="LogFilter"/> で絞り込む。</summary>
    public ICollectionView LogView { get; }

    public GitSessionViewModel(GitService git, IEditorService editor, DiffSessionViewModel diff)
    {
        _git = git;
        _editor = editor;
        _diff = diff;
        LogView = CollectionViewSource.GetDefaultView(LogRows);
        LogView.Filter = FilterLogRow;
        _git.RepositoryChanged += OnRepositoryChanged;
    }

    /// <summary>絞り込み式が変わったら解析し直してビューを更新する。</summary>
    partial void OnLogFilterChanged(string value)
    {
        _parsedFilter = CommitLogFilter.Parse(value);
        RefreshLogView();
    }

    partial void OnAuthorSelectionChanged(string value) => RefreshLogView();
    partial void OnDateFromChanged(DateTime? value) => RefreshLogView();
    partial void OnDateToChanged(DateTime? value) => RefreshLogView();

    private void RefreshLogView()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        LogView.Refresh();
    }

    /// <summary>すべての絞り込み（式・作者・期間）を解除する。</summary>
    [RelayCommand]
    private void ClearLogFilters()
    {
        LogFilter = "";
        AuthorSelection = AllAuthorsLabel;
        DateFrom = null;
        DateTo = null;
    }

    /// <summary>期間フィルタ（開始日・終了日）だけを解除する。</summary>
    [RelayCommand]
    private void ClearDateFilter()
    {
        DateFrom = null;
        DateTo = null;
    }

    /// <summary>1行がフィルタに合致するか。何も指定が無ければ全件通す。
    /// 絞り込み中はグラフ継続だけの行（ハッシュ無し）は隠す。</summary>
    private bool FilterLogRow(object item)
    {
        if (!HasActiveFilters) return true;
        if (item is not GitLogRow { IsCommit: true } row) return false;
        if (!_parsedFilter.Matches(row)) return false;
        if (EffectiveAuthor is { } author && !string.Equals(row.Author, author, StringComparison.Ordinal))
            return false;
        return MatchesDateRange(row);
    }

    /// <summary>作者ドロップダウン・期間ピッカーによる日付範囲（両端含む・日単位）に合致するか。</summary>
    private bool MatchesDateRange(GitLogRow row)
    {
        if (!DateFrom.HasValue && !DateTo.HasValue) return true;
        var day = CommitLogFilter.DayOf(row);
        if (day is null) return false;
        if (DateFrom is { } from && string.CompareOrdinal(day, from.ToString("yyyy-MM-dd")) < 0) return false;
        if (DateTo is { } to && string.CompareOrdinal(day, to.ToString("yyyy-MM-dd")) > 0) return false;
        return true;
    }

    /// <summary>Diff セッションへの表示を要求した（ShellWindow が Diff ペインを表示・フォーカスする）。</summary>
    public event EventHandler? DiffOpenRequested;

    /// <summary>
    /// リポジトリ状態が変わった可能性がある（<see cref="GitService.RepositoryChanged"/> をそのまま中継）。
    /// ShellWindow はこれで開いているエディタタブをディスクの最新内容へ追従させる（チェックアウト等で
    /// ファイルが書き換わる／消える／元に戻るケースの取りこぼし対策）。UI スレッドとは限らないので
    /// 購読側でディスパッチすること。
    /// </summary>
    public event EventHandler? RepositoryChanged
    {
        add => _git.RepositoryChanged += value;
        remove => _git.RepositoryChanged -= value;
    }

    private IDisposable? _live;

    /// <summary>Git ペインが初めて表示されたときに読み込む（以降は RepositoryChanged で追従）。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    /// <summary>Git ペインが見えている間のライブ監視を開始する。</summary>
    public void StartLiveTracking() => _live ??= _git.TrackLiveChanges();

    /// <summary>Git ペインが隠れたらライブ監視を止める。</summary>
    public void StopLiveTracking()
    {
        _live?.Dispose();
        _live = null;
    }

    private void OnRepositoryChanged(object? sender, EventArgs e)
    {
        if (!_loaded) return;
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
    }

    private async Task RefreshAsync()
    {
        _loaded = true;

        var workspaceRoot = _git.RootPath;
        if (_lastWorkspaceRoot is not null &&
            !string.Equals(_lastWorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            ResetPathScope();
        }
        _lastWorkspaceRoot = workspaceRoot;

        _status = await _git.GetStatusAsync();
        IsRepository = _status.IsRepository;

        if (!_status.IsRepository)
        {
            BranchLabel = "";
            _allBranches = Array.Empty<GitBranchInfo>();
            BranchTree = Array.Empty<BranchTreeNode>();
            FilteredBranchTree = BranchTree;
            RemoteLabel = "";
            UpstreamLabel = "";
            Ahead = Behind = 0;
            Tags = Array.Empty<GitTagInfo>();
            Submodules = Array.Empty<GitSubmoduleInfo>();
            LogRows.Clear();
            UpdateAuthorOptions();
            CommitDetail = "";
            OperationInProgress = false;
            ResetPathScope();
            return;
        }

        Ahead = _status.Ahead;
        Behind = _status.Behind;
        BranchLabel = (Ahead, Behind) switch
        {
            (0, 0) => _status.Branch,
            (var a, 0) => $"{_status.Branch} ↑{a}",
            (0, var b) => $"{_status.Branch} ↓{b}",
            var (a, b) => $"{_status.Branch} ↑{a} ↓{b}",
        };

        OperationInProgress = _status.OperationInProgress;
        OperationLabel = _status.RebaseInProgress ? "リベースが進行中です（コンフリクトを解消してください）"
            : _status.MergeInProgress ? "マージが進行中です（コンフリクトを解消してください）"
            : _status.CherryPickInProgress ? "チェリーピックが進行中です（コンフリクトを解消してください）"
            : "";
        OperationCanSkip = _status.RebaseInProgress || _status.CherryPickInProgress;

        // 構成が変わらなければ同一インスタンスが返り、ビュー（開閉・選択）はそのまま保たれる
        _allBranches = await _git.GetBranchesAsync();
        BranchTree = BranchTreeBuilder.Update(BranchTree, _allBranches);
        UpdateFilteredBranchTree();
        UpdateRemote(await _git.GetRemotesAsync());
        Tags = await _git.GetTagsAsync();
        Submodules = await _git.GetSubmodulesAsync();

        await ReloadLogAsync();
    }

    /// <summary>
    /// 同期の相手（リモート・上流）を決める。上流があればそのリモート、無ければ最初のリモート
    /// （push が -u origin HEAD で作りに行く先と一致させる）。
    /// </summary>
    private void UpdateRemote(IReadOnlyList<string> remotes)
    {
        var current = _allBranches.FirstOrDefault(b => b.IsCurrent);
        UpstreamLabel = current?.Upstream ?? "";

        var fromUpstream = UpstreamLabel.Length > 0 ? UpstreamLabel.Split('/')[0] : null;
        RemoteLabel = fromUpstream is not null && remotes.Contains(fromUpstream)
            ? fromUpstream
            : remotes.FirstOrDefault() ?? "";
    }

    partial void OnBranchFilterChanged(string value) => UpdateFilteredBranchTree();

    /// <summary>空語のときは <see cref="BranchTree"/> をそのまま指す（作り直さない＝開閉状態が生きる）。</summary>
    private void UpdateFilteredBranchTree() =>
        FilteredBranchTree = string.IsNullOrWhiteSpace(BranchFilter)
            ? BranchTree
            : BranchTreeBuilder.BuildFiltered(_allBranches, BranchFilter);

    /// <summary>コミットグラフだけを（現在の表示ブランチ範囲で）先頭ページから読み直す。選択は可能なら維持する。</summary>
    private async Task ReloadLogAsync()
    {
        var selectedHash = SelectedLogRow?.Hash;
        LogRows.Clear();
        _loadedCommitCount = 0;
        var reselect = await AppendLogPageAsync(selectedHash);
        SelectedLogRow = reselect;
        if (reselect is null)
            CommitDetail = "";

        UpdateAuthorOptions();
    }

    /// <summary>
    /// 末尾スクロールでの追加読み込み：次の1ページを一覧の末尾へ足す。
    /// 追加読み込み中・これ以上ない場合は何もしない（多重発火はここで弾く）。
    /// </summary>
    public async Task LoadMoreLogAsync()
    {
        if (IsLoadingMoreLog || !HasMoreLog)
            return;
        IsLoadingMoreLog = true;
        try
        {
            await AppendLogPageAsync(SelectedLogRow?.Hash);
            UpdateAuthorOptions();
        }
        finally
        {
            IsLoadingMoreLog = false;
        }
    }

    /// <summary>
    /// 現在の <see cref="_loadedCommitCount"/> を skip として1ページ分読み、末尾へ追加する。
    /// <see cref="HasMoreLog"/> を更新し、<paramref name="reselectHash"/> と一致する行があれば返す（選択維持用）。
    /// </summary>
    private async Task<GitLogRow?> AppendLogPageAsync(string? reselectHash)
    {
        var page = await _git.GetLogAsync(_logBranch, LogPageSize, _loadedCommitCount, _logPath);
        var commitsInPage = 0;
        GitLogRow? reselect = null;
        foreach (var row in page)
        {
            LogRows.Add(row);
            if (row.IsCommit)
            {
                commitsInPage++;
                _loadedCommitCount++;
            }
            if (reselectHash is not null && row.Hash == reselectHash)
                reselect = row;
        }
        // 満ページ（依頼件数ちょうど）なら、まだ続きがあるとみなす。
        HasMoreLog = commitsInPage >= LogPageSize;
        return reselect;
    }

    /// <summary>読み込み済みログの作者を作者ドロップダウンへ反映する。選択中の作者が消えたら「すべて」へ戻す。</summary>
    private void UpdateAuthorOptions()
    {
        var authors = LogRows
            .Where(r => r.IsCommit && !string.IsNullOrEmpty(r.Author))
            .Select(r => r.Author!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var options = new List<string>(authors.Count + 1) { AllAuthorsLabel };
        options.AddRange(authors);
        AuthorOptions = options;

        // 選択中の作者が今回のログに居なければ「すべて」へ戻す（ブランチ切替で作者集合が変わるため）。
        if (!options.Contains(AuthorSelection, StringComparer.Ordinal))
            AuthorSelection = AllAuthorsLabel;
    }

    /// <summary>
    /// ブランチ一覧のダブルクリック：チェックアウト（作業ブランチの変更）はせず、
    /// 右側のコミットグラフをそのブランチの内容に切り替えるだけ。
    /// </summary>
    public Task ShowBranchLogAsync(GitBranchInfo branch)
    {
        _logBranch = branch.Name;
        IsLogScoped = true;
        return ReloadLogAsync();
    }

    /// <summary>コミットグラフを全ブランチ（--all）表示に戻す。</summary>
    public Task ShowAllBranchesLogAsync()
    {
        _logBranch = null;
        IsLogScoped = false;
        return ReloadLogAsync();
    }

    /// <summary>
    /// 指定パス（ファイル／フォルダのフルパス）を変更したコミットだけにコミットグラフを絞る
    /// （FolderTree／エディタの「履歴を表示」の合流点）。ペイン未読込ならフル読込（RefreshAsync）を、
    /// 読込済みならコミットグラフだけ読み直す。ブランチ範囲の絞り込みとは併用される。
    /// </summary>
    public Task ShowPathHistoryAsync(string fullPath)
    {
        var root = _git.RootPath;
        if (string.IsNullOrEmpty(root))
            return Task.CompletedTask;

        _logPath = System.IO.Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        PathScopeLabel = _logPath;
        IsPathScoped = true;

        if (!_loaded)
        {
            _loaded = true;
            return RefreshAsync();
        }
        return ReloadLogAsync();
    }

    /// <summary>パスの履歴絞り込みを解除して全コミット表示に戻す（履歴スコープ帯の「✕」）。</summary>
    [RelayCommand]
    private Task ClearPathScope()
    {
        ResetPathScope();
        return ReloadLogAsync();
    }

    private void ResetPathScope()
    {
        _logPath = null;
        IsPathScoped = false;
        PathScopeLabel = "";
    }

    partial void OnSelectedLogRowChanged(GitLogRow? value)
    {
        if (value?.Hash is not { } hash)
            return;
        _ = LoadCommitDetailAsync(hash);
    }

    private async Task LoadCommitDetailAsync(string hash)
    {
        CommitDetail = await _git.GetCommitSummaryAsync(hash);
    }

    /// <summary>
    /// コミット詳細（変更ファイル一覧）でクリックされた相対パスを、リポジトリルート基準で解決し
    /// エディタで開く。現在の作業ツリーに存在しない（削除済み・過去の名前）場合はメッセージのみ。
    /// </summary>
    public async Task OpenChangedFileAsync(string relativePath)
    {
        var root = _git.RootPath;
        if (string.IsNullOrEmpty(root)) return;

        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath));
        if (!System.IO.File.Exists(full))
        {
            StatusIsError = true;
            StatusMessage = $"ファイルが見つかりません: {relativePath}";
            return;
        }
        await _editor.OpenFileAsync(full);
    }

    // ===== 同期 =====

    [RelayCommand] private Task FetchAsync() => RunOpAsync("フェッチ", () => _git.FetchAsync());
    [RelayCommand] private Task PullAsync() => RunOpAsync("プル", () => _git.PullAsync());
    [RelayCommand] private Task PushAsync() => RunOpAsync("プッシュ", () => _git.PushAsync());

    // ===== ブランチ操作（対象はビューから引数で渡す） =====

    public Task<GitCommandResult?> CheckoutBranchAsync(GitBranchInfo branch) => branch.IsRemote
        ? RunOpAsync($"チェックアウト {branch.Name}", () => _git.CheckoutTrackAsync(branch.Name))
        : RunOpAsync($"チェックアウト {branch.Name}", () => _git.CheckoutAsync(branch.Name));

    public Task<GitCommandResult?> CreateBranchAsync(string name, string? startPoint = null) =>
        RunOpAsync($"ブランチ作成 {name}", () => _git.CreateBranchAsync(name, startPoint));

    public Task<GitCommandResult?> DeleteBranchAsync(GitBranchInfo branch, bool force) =>
        RunOpAsync($"ブランチ削除 {branch.Name}", () => _git.DeleteBranchAsync(branch.Name, force));

    public async Task<GitCommandResult?> MergeAsync(GitBranchInfo branch, GitMergeStrategy strategy = GitMergeStrategy.Default)
    {
        var label = strategy switch
        {
            GitMergeStrategy.FastForwardOnly => $"{branch.Name} をFast-forwardのみでマージ",
            GitMergeStrategy.NoFastForward => $"{branch.Name} をマージコミットを作成してマージ",
            GitMergeStrategy.Squash => $"{branch.Name} をスカッシュマージ",
            _ => $"{branch.Name} をマージ"
        };
        var result = await RunOpAsync(label, () => _git.MergeAsync(branch.Name, strategy));
        // スカッシュは git merge --squash の挙動どおりステージするだけでコミットは作らない。
        // 自動コミットはせず、既存のコミットUI（ステージ済み変更のコミット操作）へ誘導する。
        if (result is { Success: true } && strategy == GitMergeStrategy.Squash)
            StatusMessage = $"{label}してステージしました。内容を確認してコミットしてください。";
        return result;
    }

    public Task<GitCommandResult?> RebaseAsync(GitBranchInfo branch) =>
        RunOpAsync($"{branch.Name} へリベース", () => _git.RebaseAsync(branch.Name));

    // ===== インタラクティブリベース =====

    public Task<(IReadOnlyList<RebasePlanEntry> Entries, string? Error)> GetRebaseCandidatesAsync(GitLogRow row) =>
        row.Hash is null
            ? Task.FromResult<(IReadOnlyList<RebasePlanEntry>, string?)>((Array.Empty<RebasePlanEntry>(), null))
            : _git.GetRebaseCandidatesAsync(row.Hash);

    public Task<GitCommandResult?> InteractiveRebaseAsync(
        string fromHash, IReadOnlyList<RebasePlanEntry> plan, IReadOnlyDictionary<string, string> messages) =>
        RunOpAsync("インタラクティブリベース", () => _git.InteractiveRebaseAsync(fromHash, plan, messages));

    // ===== タグ操作（対象はビューから引数で渡す） =====

    public Task<GitCommandResult?> CreateTagAsync(string name, string? target, string? message) =>
        RunOpAsync($"タグ作成 {name}", () => _git.CreateTagAsync(name, target, message));

    public Task<GitCommandResult?> DeleteTagAsync(GitTagInfo tag) =>
        RunOpAsync($"タグ削除 {tag.Name}", () => _git.DeleteTagAsync(tag.Name));

    public Task<GitCommandResult?> PushTagAsync(GitTagInfo tag) =>
        RunOpAsync($"タグ {tag.Name} をプッシュ", () => _git.PushTagAsync(tag.Name));

    public Task<GitCommandResult?> PushAllTagsAsync() =>
        RunOpAsync("すべてのタグをプッシュ", () => _git.PushAllTagsAsync());

    public Task<GitCommandResult?> CheckoutTagAsync(GitTagInfo tag) =>
        RunOpAsync($"チェックアウト {tag.Name}", () => _git.CheckoutCommitAsync(tag.Name));

    // ===== サブモジュール操作（対象はビューから引数で渡す） =====

    public Task<GitCommandResult?> InitSubmoduleAsync(GitSubmoduleInfo submodule) =>
        RunOpAsync($"サブモジュール初期化 {submodule.Path}", () => _git.SubmoduleInitAsync(submodule.Path));

    public Task<GitCommandResult?> UpdateSubmoduleAsync(GitSubmoduleInfo submodule) =>
        RunOpAsync($"サブモジュール更新 {submodule.Path}", () => _git.SubmoduleUpdateAsync(submodule.Path));

    public Task<GitCommandResult?> SyncSubmodulesAsync() =>
        RunOpAsync("サブモジュール同期", () => _git.SubmoduleSyncAsync());

    // ===== コミット操作 =====

    public Task<GitCommandResult?> CheckoutCommitAsync(GitLogRow row) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunOpAsync($"チェックアウト {row.ShortHash}", () => _git.CheckoutCommitAsync(row.Hash));

    public Task<GitCommandResult?> CherryPickAsync(GitLogRow row) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunOpAsync($"チェリーピック {row.ShortHash}", () => _git.CherryPickAsync(row.Hash));

    public Task<GitCommandResult?> RevertAsync(GitLogRow row) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunOpAsync($"リバート {row.ShortHash}", () => _git.RevertAsync(row.Hash));

    public Task<GitCommandResult?> ResetAsync(GitLogRow row, GitResetMode mode) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunOpAsync($"リセット（{mode.ToString().ToLowerInvariant()}）{row.ShortHash}",
            () => _git.ResetAsync(row.Hash, mode));

    public Task<string> GetCommitMessageAsync(GitLogRow row) => row.Hash is null
        ? Task.FromResult("")
        : _git.GetCommitMessageAsync(row.Hash);

    public Task<GitCommandResult?> RewriteCommitMessageAsync(GitLogRow row, string message) => row.Hash is null
        ? Task.FromResult<GitCommandResult?>(null)
        : RunOpAsync($"コミットメッセージ修正 {row.ShortHash}",
            () => _git.RewriteCommitMessageAsync(row.Hash, message));

    public async Task<string> GetCombinedCommitMessageAsync(IReadOnlyList<GitLogRow> rows)
    {
        var commits = rows.Where(r => r.Hash is not null)
            .OrderByDescending(r => LogRows.IndexOf(r)) // 一覧は新しい順なので、古いコミットから連結
            .ToList();
        var messages = await Task.WhenAll(commits.Select(c => _git.GetCommitMessageAsync(c.Hash!)));
        return string.Join("\n\n", messages.Where(m => !string.IsNullOrWhiteSpace(m)));
    }

    /// <summary>選択した連続コミット群を1つにまとめる（squash）。2件未満なら何もしない。</summary>
    public Task<GitCommandResult?> SquashAsync(IReadOnlyList<GitLogRow> rows, string commitMessage)
    {
        var hashes = rows.Where(r => r.Hash is not null).Select(r => r.Hash!).ToList();
        if (hashes.Count < 2)
            return Task.FromResult<GitCommandResult?>(null);
        return RunOpAsync($"スカッシュ（{hashes.Count} 件）", () => _git.SquashAsync(hashes, commitMessage));
    }

    /// <summary>
    /// 選択コミットの差分を Diff セッションで表示する。
    /// 1件ならそのコミットの変更、2件以上なら一覧上の端点（最古と最新）のスナップショット比較。
    /// </summary>
    public void OpenDiffForCommits(IReadOnlyList<GitLogRow> rows)
    {
        var commits = rows.Where(r => r.Hash is not null).ToList();
        if (commits.Count == 0)
            return;

        if (commits.Count == 1)
        {
            var c = commits[0];
            _diff.ShowCommitRange(null, c.Hash!, $"コミット {c.ShortHash}");
        }
        else
        {
            // LogRows は新しい順なので、一覧上の位置から両端（最新・最古）を決める
            var ordered = commits.OrderBy(c => LogRows.IndexOf(c)).ToList();
            var newest = ordered[0];
            var oldest = ordered[^1];
            _diff.ShowCommitRange(oldest.Hash!, newest.Hash!, $"{oldest.ShortHash} → {newest.ShortHash}");
        }
        DiffOpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>コミットのフルパッチをエディタの仮想ドキュメントで開く。</summary>
    public async Task OpenPatchAsync(GitLogRow row)
    {
        if (row.Hash is null) return;
        var patch = await _git.GetCommitPatchAsync(row.Hash);
        await _editor.OpenDocumentAsync(new EditorDocument
        {
            FileName = $"commit-{row.ShortHash}.diff",
            Content = patch,
            OnSaved = _ => { },  // 読み取り専用の用途
        });
    }

    // ===== 進行中操作（rebase / merge / cherry-pick）の続行・スキップ・中止 =====

    [RelayCommand]
    private Task ContinueOperationAsync() =>
        _status.RebaseInProgress ? RunOpAsync("リベース続行", () => _git.RebaseContinueAsync())
        : _status.CherryPickInProgress ? RunOpAsync("チェリーピック続行", () => _git.CherryPickContinueAsync())
        : _status.MergeInProgress ? RunOpAsync("マージ続行", () => _git.MergeContinueAsync())
        : Task.CompletedTask;

    [RelayCommand]
    private Task SkipOperationAsync() =>
        _status.RebaseInProgress ? RunOpAsync("リベーススキップ", () => _git.RebaseSkipAsync())
        : _status.CherryPickInProgress ? RunOpAsync("チェリーピックスキップ", () => _git.CherryPickSkipAsync())
        : Task.CompletedTask;

    [RelayCommand]
    private Task AbortOperationAsync() =>
        _status.RebaseInProgress ? RunOpAsync("リベース中止", () => _git.RebaseAbortAsync())
        : _status.CherryPickInProgress ? RunOpAsync("チェリーピック中止", () => _git.CherryPickAbortAsync())
        : _status.MergeInProgress ? RunOpAsync("マージ中止", () => _git.MergeAbortAsync())
        : Task.CompletedTask;

    /// <summary>更新系操作の共通枠：多重実行の抑止・結果メッセージの表示。実行できなければ null。</summary>
    private async Task<GitCommandResult?> RunOpAsync(string label, Func<Task<GitCommandResult>> operation)
    {
        if (IsBusy) return null;
        IsBusy = true;
        StatusMessage = $"{label}を実行中…";
        StatusIsError = false;
        try
        {
            var result = await operation();
            StatusIsError = !result.Success;
            StatusMessage = result.Success
                ? $"{label}が完了しました。"
                : Truncate(result.Message);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Truncate(string text)
    {
        var t = text.Trim();
        return t.Length <= 300 ? t : t[..300] + "…";
    }
}
