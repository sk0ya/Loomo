

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
    private readonly GitSessionQuery _query;
    public GitSessionCommandHandler Commands { get; }
    public GitHistoryViewModel History { get; }
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

    public GitSessionViewModel(GitService git, IEditorService editor, DiffSessionViewModel diff,
        GitSessionQuery query, GitSessionCommandHandler commands, GitHistoryViewModel history)
    {
        _git = git;
        _editor = editor;
        _diff = diff;
        _query = query;
        Commands = commands;
        History = history;
        Commands.StatusChanged += (_, status) =>
        {
            IsBusy = status.IsBusy;
            StatusIsError = status.IsError;
            StatusMessage = status.Message;
        };
        _git.RepositoryChanged += OnRepositoryChanged;
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
            History.ResetPathScope();
        }
        _lastWorkspaceRoot = workspaceRoot;

        var overview = await _query.LoadOverviewAsync();
        _status = overview.Status;
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
            History.Clear();
            OperationInProgress = false;
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
        _allBranches = overview.Branches;
        BranchTree = BranchTreeBuilder.Update(BranchTree, _allBranches);
        UpdateFilteredBranchTree();
        UpdateRemote(overview.Remotes);
        Tags = overview.Tags;
        Submodules = overview.Submodules;

        await History.ReloadAsync();
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

    public Task LoadMoreLogAsync() => History.LoadMoreAsync();
    public Task ShowBranchLogAsync(GitBranchInfo branch) => History.ShowBranchAsync(branch);
    public Task ShowAllBranchesLogAsync() => History.ShowAllBranchesAsync();

    public async Task ShowPathHistoryAsync(string fullPath, string? selectCommitHash = null)
    {
        var root = _git.RootPath;
        if (string.IsNullOrEmpty(root)) return;
        if (!_loaded) { _loaded = true; await RefreshAsync(); }
        await History.ShowPathAsync(root, fullPath, selectCommitHash);
    }

    public async Task SelectCommitAsync(string hash)
    {
        if (!_loaded) { _loaded = true; await RefreshAsync(); }
        await History.SelectCommitAsync(hash);
    }

    /// <summary>
    /// コミット詳細（変更ファイル一覧）でクリックされた相対パスを、リポジトリルート基準で解決し
    /// エディタで開く。現在の作業ツリーに存在しない（削除済み・過去の名前）場合はメッセージのみ。
    /// </summary>
    public async Task OpenChangedFileAsync(string relativePath)
    {
        var full = _query.ResolveExistingChangedFile(relativePath);
        if (full is null)
        {
            StatusIsError = true;
            StatusMessage = $"ファイルが見つかりません: {relativePath}";
            return;
        }
        await _editor.OpenFileAsync(full);
    }

    // ===== 同期 =====

    [RelayCommand] private Task FetchAsync() => Commands.FetchAsync();
    [RelayCommand] private Task PullAsync() => Commands.PullAsync();
    [RelayCommand] private Task PushAsync() => Commands.PushAsync();

    public async Task<string> GetCombinedCommitMessageAsync(IReadOnlyList<GitLogRow> rows)
    {
        var commits = rows.Where(r => r.Hash is not null)
            .OrderByDescending(r => History.LogRows.IndexOf(r)) // 一覧は新しい順なので、古いコミットから連結
            .ToList();
        var messages = await Task.WhenAll(commits.Select(c => Commands.GetCommitMessageAsync(c)));
        return string.Join("\n\n", messages.Where(m => !string.IsNullOrWhiteSpace(m)));
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
            var ordered = commits.OrderBy(c => History.LogRows.IndexOf(c)).ToList();
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
        var patch = await _query.GetCommitPatchAsync(row.Hash);
        await _editor.OpenDocumentAsync(new EditorDocument
        {
            FileName = $"commit-{row.ShortHash}.diff",
            Content = patch,
            OnSaved = _ => { },  // 読み取り専用の用途
        });
    }

    // ===== 進行中操作 =====

    [RelayCommand] private Task ContinueOperationAsync() => Commands.ContinueAsync(_status);
    [RelayCommand] private Task SkipOperationAsync() => Commands.SkipAsync(_status);
    [RelayCommand] private Task AbortOperationAsync() => Commands.AbortAsync(_status);

}
