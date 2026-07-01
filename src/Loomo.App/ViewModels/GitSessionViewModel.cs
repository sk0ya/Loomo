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

    /// <summary>ブランチ一覧のツリー（3個以上で "/" 区切りのフォルダ表示、未満はフラット）。</summary>
    [ObservableProperty] private IReadOnlyList<BranchTreeNode> _branchTree = Array.Empty<BranchTreeNode>();

    /// <summary>タグ一覧（作成日の新しい順、フラット表示）。</summary>
    [ObservableProperty] private IReadOnlyList<GitTagInfo> _tags = Array.Empty<GitTagInfo>();

    /// <summary>
    /// コミットグラフに表示するブランチ（ref）。null は全ブランチ（--all）。
    /// ブランチ一覧のダブルクリックで切り替わり、ヘッダーのチェックアウト（作業ブランチの変更）とは独立。
    /// </summary>
    private string? _logBranch;

    /// <summary>特定ブランチに絞っているか（「すべてのブランチを表示」リンクの表示判定）。</summary>
    [ObservableProperty] private bool _isLogScoped;

    /// <summary>コミット一覧の絞り込み語（メッセージ・作者・ハッシュ・ref を対象に部分一致）。
    /// 空なら全件。git を再実行せず、読み込み済みの一覧をクライアント側でフィルタする。</summary>
    [ObservableProperty] private string _logFilter = "";

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

    /// <summary>絞り込み語が変わったらビューを更新する。</summary>
    partial void OnLogFilterChanged(string value) => LogView.Refresh();

    /// <summary>1行がフィルタに合致するか。空語は全件通す。グラフ継続だけの行は絞り込み中は隠す。</summary>
    private bool FilterLogRow(object item)
    {
        var term = LogFilter?.Trim();
        if (string.IsNullOrEmpty(term)) return true;
        if (item is not GitLogRow row) return false;
        if (!row.IsCommit) return false;  // 絞り込み中はグラフ継続行（ハッシュ無し）を隠す
        return Contains(row.Subject, term)
            || Contains(row.Author, term)
            || Contains(row.ShortHash, term)
            || Contains(row.Hash, term)
            || Contains(row.Refs, term);
    }

    private static bool Contains(string? haystack, string term) =>
        haystack is not null && haystack.Contains(term, StringComparison.OrdinalIgnoreCase);

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
        _status = await _git.GetStatusAsync();
        IsRepository = _status.IsRepository;

        if (!_status.IsRepository)
        {
            BranchLabel = "";
            BranchTree = Array.Empty<BranchTreeNode>();
            Tags = Array.Empty<GitTagInfo>();
            LogRows.Clear();
            CommitDetail = "";
            OperationInProgress = false;
            return;
        }

        BranchLabel = (_status.Ahead, _status.Behind) switch
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
        var branches = await _git.GetBranchesAsync();
        BranchTree = BranchTreeBuilder.Update(BranchTree, branches);
        Tags = await _git.GetTagsAsync();

        await ReloadLogAsync();
    }

    /// <summary>コミットグラフだけを（現在の表示ブランチ範囲で）読み直す。選択は可能なら維持する。</summary>
    private async Task ReloadLogAsync()
    {
        var selectedHash = SelectedLogRow?.Hash;
        var log = await _git.GetLogAsync(_logBranch);
        LogRows.Clear();
        GitLogRow? reselect = null;
        foreach (var row in log)
        {
            LogRows.Add(row);
            if (selectedHash is not null && row.Hash == selectedHash)
                reselect = row;
        }
        SelectedLogRow = reselect;
        if (reselect is null)
            CommitDetail = "";
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

    public Task<GitCommandResult?> MergeAsync(GitBranchInfo branch) =>
        RunOpAsync($"{branch.Name} をマージ", () => _git.MergeAsync(branch.Name));

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
