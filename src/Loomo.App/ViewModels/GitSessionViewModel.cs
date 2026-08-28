

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
    private readonly LoomoSettings? _settings;
    private readonly SettingsStore? _settingsStore;
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
    /// <summary>現在のリポジトリがGitHub.comにある場合のWeb URL。</summary>
    [ObservableProperty] private string? _gitHubRepositoryUrl;

    public bool IsGitHubRepository => !string.IsNullOrWhiteSpace(GitHubRepositoryUrl);
    public string GitHubRepositoryLabel => GitHubRepositoryUrl is { } url
        ? new Uri(url).AbsolutePath.Trim('/')
        : "";

    /// <summary>rebase / merge / cherry-pick が進行中か（続行・中止バナーの表示）。</summary>
    [ObservableProperty] private bool _operationInProgress;
    [ObservableProperty] private string _operationLabel = "";
    /// <summary>進行中操作が「スキップ」を持つか（rebase / cherry-pick のみ）。</summary>
    [ObservableProperty] private bool _operationCanSkip;

    /// <summary>下段のコミット詳細（選択コミットの <c>git show --stat</c>）を表示するか。
    /// Git ペインのタイトル領域のトグルで切り替え、設定へ永続化する。</summary>
    [ObservableProperty] private bool _commitDetailVisible = true;

    partial void OnCommitDetailVisibleChanged(bool value)
    {
        if (_settings is null) return;
        _settings.GitCommitDetailVisible = value;
        try { _settingsStore?.Save(_settings); }
        catch { /* 永続化に失敗しても表示切替自体は効かせる */ }
    }

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

    /// <summary>リモート一覧（名前＋fetch URL）。追加・URL 変更・削除の対象。</summary>
    [ObservableProperty] private IReadOnlyList<GitRemoteInfo> _remotes = Array.Empty<GitRemoteInfo>();

    /// <summary>サブモジュール一覧（0件ならビュー側でセクションごと隠す）。</summary>
    [ObservableProperty] private IReadOnlyList<GitSubmoduleInfo> _submodules = Array.Empty<GitSubmoduleInfo>();

    /// <summary>Git 操作の対象フォルダーの切替 UI 状態。サイドバー Git パネル（<see cref="GitPanelViewModel"/>）
    /// と共有する（どちらから切り替えても両方に反映される）。</summary>
    public GitRootSwitchViewModel RootSwitch { get; }

    public GitSessionViewModel(GitService git, IEditorService editor, DiffSessionViewModel diff,
        GitSessionQuery query, GitSessionCommandHandler commands, GitHistoryViewModel history,
        GitRootSwitchViewModel rootSwitch,
        LoomoSettings? settings = null, SettingsStore? settingsStore = null)
    {
        _git = git;
        _editor = editor;
        _diff = diff;
        _query = query;
        Commands = commands;
        History = history;
        RootSwitch = rootSwitch;
        _settings = settings;
        _settingsStore = settingsStore;
        // 保存された表示状態を初期反映する（field 直接代入なので OnCommitDetailVisibleChanged＝永続化は走らない）。
        _commitDetailVisible = settings?.GitCommitDetailVisible ?? true;
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

    /// <summary>コミット詳細の1ファイルを、独立した差分ウィンドウで見たい
    /// （ShellWindow が切り離しウィンドウを開く）。ペインの表示対象は動かさない。</summary>
    public event EventHandler<CommitFileDiffRequest>? DiffWindowRequested;

    /// <summary>GitHubのページを内蔵ブラウザで開く要求。</summary>
    public event EventHandler<string>? OpenHostingUrlRequested;

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
            GitHubRepositoryUrl = null;
            Tags = Array.Empty<GitTagInfo>();
            Remotes = Array.Empty<GitRemoteInfo>();
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
        var remoteUrls = await _git.GetRemoteUrlsAsync();
        Remotes = remoteUrls;
        GitHubRepositoryUrl = remoteUrls
            .OrderBy(remote => string.Equals(remote.Name, "origin", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(remote => GitHostingUrl.TryGetGitHubRepositoryUrl(remote.Url, out var url) ? url : null)
            .FirstOrDefault(url => url is not null);
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

    partial void OnGitHubRepositoryUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(IsGitHubRepository));
        OnPropertyChanged(nameof(GitHubRepositoryLabel));
    }

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
        // マルチルート：fullPath が現在の対象と違うワークスペースフォルダーに属していたら、
        // そのフォルダーのリポジトリへ Git 操作対象を切り替えてから履歴を引く
        // （さもないと現在の対象リポジトリへ誤って git log -- path が飛ぶ）。
        _git.SetActiveRootForPath(fullPath);
        var root = _git.RootPath;
        if (string.IsNullOrEmpty(root)) return;
        _loaded = true;
        await RefreshAsync();
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

    /// <summary>取り込み方を選んでプルする（ビューのプルボタンのメニューから）。</summary>
    public Task<GitCommandResult?> PullWithModeAsync(GitPullMode mode) => Commands.PullAsync(mode);

    /// <summary>強制プッシュ（<c>--force-with-lease</c>）。確認ダイアログはビュー側で出す。</summary>
    public Task<GitCommandResult?> PushForceAsync() => Commands.PushForceAsync();

    public Task<GitCommandResult?> PullBranchAsync(GitBranchInfo branch) =>
        Commands.PullBranchAsync(branch);

    public Task<GitCommandResult?> PushBranchAsync(GitBranchInfo branch, bool force = false) =>
        Commands.PushBranchAsync(branch, RemoteLabel, force);

    public Task<GitCommandResult?> DeleteRemoteBranchAsync(GitBranchInfo branch) =>
        Commands.DeleteRemoteBranchAsync(branch);

    public Task<GitCommandResult?> SetUpstreamAsync(GitBranchInfo branch, string upstream) =>
        Commands.SetUpstreamAsync(branch, upstream);

    public Task<GitCommandResult?> UnsetUpstreamAsync(GitBranchInfo branch) =>
        Commands.UnsetUpstreamAsync(branch);

    /// <summary>
    /// 上流の候補（リモート追跡ブランチ名）。上流を設定するダイアログの初期値・候補表示に使う。
    /// </summary>
    public IReadOnlyList<string> RemoteBranchNames =>
        _allBranches.Where(branch => branch.IsRemote).Select(branch => branch.Name).ToList();

    public Task<GitCommandResult?> AddRemoteAsync(string name, string url) =>
        Commands.AddRemoteAsync(name, url);

    public Task<GitCommandResult?> SetRemoteUrlAsync(string name, string url) =>
        Commands.SetRemoteUrlAsync(name, url);

    public Task<GitCommandResult?> RemoveRemoteAsync(string name) => Commands.RemoveRemoteAsync(name);

    [RelayCommand]
    private void OpenPullRequests()
    {
        if (GitHubRepositoryUrl is { } url)
            OpenHostingUrlRequested?.Invoke(this, $"{url}/pulls");
    }

    [RelayCommand]
    private void OpenIssues()
    {
        if (GitHubRepositoryUrl is { } url)
            OpenHostingUrlRequested?.Invoke(this, $"{url}/issues");
    }

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

    /// <summary>
    /// コミット詳細の変更ファイル一覧から、そのファイル1つの差分を<b>別ウィンドウ</b>で開く。
    /// 対象は「一覧で選んでいるコミット × 渡されたパス」。削除されたファイルでも差分は見られるので、
    /// 作業ツリーに実体があるかは問わない（<see cref="GitSessionQuery.ToFullPath"/> で素直に解決する）。
    /// </summary>
    public void RequestChangedFileDiffWindow(string relativePath)
    {
        if (History.SelectedLogRow is not { Hash: { } hash } row) return;
        if (_query.ToFullPath(relativePath) is not { } fullPath) return;
        DiffWindowRequested?.Invoke(this,
            new CommitFileDiffRequest(hash, $"コミット {row.ShortHash}", fullPath));
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

    // ===== 特定リビジョンのファイル =====
    //
    // ファイル履歴（パス絞り込み）中だけ意味を持つ操作。Rider の File History と同じ3点——
    // その頃の中身を「開く」「今と比べる」「戻す」。コミット詳細の変更ファイル一覧が
    // 現在の版しか開けなかったので、過去の版に手が届く経路がここまで無かった。

    /// <summary>ファイル履歴を見ている（＝この節の操作が使える）か。</summary>
    public bool IsFileHistory => History.IsFileScoped;

    /// <summary>そのコミット時点の内容をエディタの仮想ドキュメントで開く（読み取り専用の用途）。</summary>
    public async Task OpenFileAtRevisionAsync(GitLogRow row)
    {
        if (await RevisionTargetAsync(row) is not { } target) return;
        var (hash, shortHash, path) = target;

        var result = await _query.GetFileAtRevisionAsync(hash, path);
        if (!result.Success)
        {
            SetError(result.Message);
            return;
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = System.IO.Path.GetExtension(path);
        await _editor.OpenDocumentAsync(new EditorDocument
        {
            FileName = $"{name}@{shortHash}{extension}",
            Content = result.Output,
            OnSaved = _ => { },  // 過去の版なので保存先は無い
        });
    }

    /// <summary>
    /// そのコミット時点の内容と、いまの作業ツリーの内容を Diff ペインで見比べる。
    /// git の差分テキストではなく<b>2つの本文の比較</b>として渡すので、左右入替や行ジャンプなど
    /// アドホック比較の道具立てがそのまま効く。
    /// </summary>
    public async Task CompareFileWithRevisionAsync(GitLogRow row)
    {
        if (await RevisionTargetAsync(row) is not { } target) return;
        var (hash, shortHash, path) = target;

        var result = await _query.GetFileAtRevisionAsync(hash, path);
        if (!result.Success)
        {
            SetError(result.Message);
            return;
        }

        var fullPath = _query.ToFullPath(path);
        var (currentTitle, currentText) = await ReadWorkingTreeAsync(fullPath);
        var name = System.IO.Path.GetFileName(path);
        _diff.ShowComparison(new DiffComparison(
            $"{name}@{shortHash}", result.Output, currentTitle, currentText,
            FilePath: fullPath ?? "", FileIsLeft: false));
        DiffOpenRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>作業ツリーのファイルをそのコミット時点の内容へ戻す（確認はビュー側で済ませて呼ぶ）。</summary>
    public async Task RestoreFileAtRevisionAsync(GitLogRow row)
    {
        if (await RevisionTargetAsync(row) is not { } target) return;
        var (hash, shortHash, path) = target;
        // path はその版での名前。書き戻し先はいまの名前なので、両方を渡す。
        await Commands.RestoreFileAtRevisionAsync(hash, shortHash, path, History.ScopedPath);
    }

    /// <summary>この節の操作の前提（ファイル履歴＋コミット行）が揃っているか。揃っていなければ null。
    ///
    /// <para>パスは<b>そのコミット時点の名前</b>を返す。ファイル履歴は <c>--follow</c> でリネームを
    /// 追って並べているので、いまの名前のまま <c>git show &lt;hash&gt;:&lt;path&gt;</c> を投げると、
    /// リネーム前の行では必ず「このファイルはありません」になる（追跡で拾えるようにした行が、
    /// そっくり操作できない行になっていた）。</para></summary>
    private async Task<(string Hash, string ShortHash, string Path)?> RevisionTargetAsync(GitLogRow row)
    {
        if (row.Hash is not { } hash || History.ScopedPath is not { Length: > 0 } path
            || !History.IsFileScoped)
            return null;
        return (hash, row.ShortHash ?? hash[..Math.Min(7, hash.Length)],
            await ResolveRevisionPathAsync(hash, path));
    }

    /// <summary>いまのパス絞り込みに対するリネーム追跡表（引けたら覚えておく。パスが変われば作り直す）。</summary>
    private string? _renameTrailPath;
    private IReadOnlyDictionary<string, string> _renameTrail =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private async Task<string> ResolveRevisionPathAsync(string hash, string currentPath)
    {
        if (!string.Equals(_renameTrailPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            _renameTrail = await _query.GetRenameTrailAsync(currentPath);
            _renameTrailPath = currentPath;
        }
        // 表に無いコミット（追跡が使えない git 構成・表が引けなかった）はいまの名前で試す。
        return _renameTrail.TryGetValue(hash, out var atRevision) ? atRevision : currentPath;
    }

    private static async Task<(string Title, string Text)> ReadWorkingTreeAsync(string? fullPath)
    {
        if (fullPath is null || !System.IO.File.Exists(fullPath))
            return ("現在（作業ツリーに無し）", "");
        try
        {
            return ("現在", await System.IO.File.ReadAllTextAsync(fullPath));
        }
        catch (Exception exception)
        {
            return ("現在（読み取り失敗）", exception.Message);
        }
    }

    private void SetError(string message)
    {
        StatusIsError = true;
        StatusMessage = message;
    }

    // ===== 進行中操作 =====

    [RelayCommand] private Task ContinueOperationAsync() => Commands.ContinueAsync(_status);
    [RelayCommand] private Task SkipOperationAsync() => Commands.SkipAsync(_status);
    [RelayCommand] private Task AbortOperationAsync() => Commands.AbortAsync(_status);

}

/// <summary>1コミットの1ファイルを差分ウィンドウで開く要求。</summary>
/// <param name="Hash">対象コミット（親との差分を見る）。</param>
/// <param name="Label">ウィンドウ・ヘッダーに出す対象の呼び名（「コミット 0c92f1e」）。</param>
/// <param name="FullPath">対象ファイルの絶対パス。マルチルートで対象リポジトリを決めるのにも使う。</param>
public readonly record struct CommitFileDiffRequest(string Hash, string Label, string FullPath);
