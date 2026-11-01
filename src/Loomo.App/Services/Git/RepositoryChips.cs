using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>リポジトリの印を押したときに頼む操作（§40.7）。</summary>
public enum RepositoryActionKind
{
    /// <summary>そのリポジトリを Git の対象にしてブランチ切替を開く。</summary>
    SwitchBranch,
    /// <summary>そのリポジトリで fetch。</summary>
    Fetch,
    /// <summary>そのリポジトリで pull。</summary>
    Pull,
    /// <summary>Git パネルでそのリポジトリを開く（変更の一覧）。</summary>
    OpenChanges,
    /// <summary>Git ペインでそのリポジトリのログを開く。</summary>
    OpenLog,
    /// <summary>可視ターミナルでそのリポジトリの根へ移る。</summary>
    OpenTerminal,
    /// <summary>ファイル一覧でそのリポジトリの根を開く。</summary>
    OpenInFiles,
}

/// <summary>リポジトリの操作の頼み。</summary>
public sealed record RepositoryActionRequest(string RepositoryRoot, RepositoryActionKind Kind);

/// <summary>
/// リポジトリの印（<see cref="RepositoryChipViewModel"/>）を押したときの動作の<b>唯一の</b>受け口（§40.7）。
/// 印はツリー・ファイル一覧・Git パネル・ステータスに出るが、押したときの動作を面ごとに書くと、片方だけ押せる／
/// 片方だけ対象が違う、という食い違いが必ず出る（§26.10 で操作の実体をツリーと共有したのと同じ理由）。
/// fetch／pull はここで実行し（対象を明示した窓口・§40.5）、画面の切替が要るもの（パネル・ペイン・ターミナル）は
/// <see cref="Requested"/> で ShellWindow へ頼む。
/// </summary>
public sealed class RepositoryActions
{
    private readonly GitService _git;

    public RepositoryActions(GitService git) => _git = git;

    /// <summary>画面の切替が要る操作の頼み（ShellWindow が受ける）。UI スレッドで鳴る。</summary>
    public event EventHandler<RepositoryActionRequest>? Requested;

    /// <summary>操作を頼む。fetch／pull はここで実行して結果を返し、それ以外は <see cref="Requested"/> へ流して null。</summary>
    public async Task<GitCommandResult?> ExecuteAsync(string repositoryRoot, RepositoryActionKind kind)
    {
        var root = WorkspacePaths.Normalize(repositoryRoot);
        switch (kind)
        {
            case RepositoryActionKind.Fetch:
                return await GitFor(root).FetchAsync();
            case RepositoryActionKind.Pull:
                return await GitFor(root).PullAsync();
            default:
                Requested?.Invoke(this, new RepositoryActionRequest(root, kind));
                return null;
        }
    }

    /// <summary>Git パネルが見ているリポジトリならパネルと同じ窓口で——結果（ahead/behind・変更）がパネルへ
    /// 届くように。それ以外はリポジトリに固定した窓口。</summary>
    private GitService GitFor(string root)
    {
        var selected = _git.RootPath is { } current ? _git.Repositories.RepositoryFor(current)?.Root : null;
        return string.Equals(selected, root, StringComparison.OrdinalIgnoreCase) ? _git : _git.For(root);
    }
}

/// <summary>
/// リポジトリ1つの印（§40.7）：<c>⎇ main  ↑2 ↓3  ±12  REBASING  ⚠3</c>。ブランチ名は常に、それ以外は 0 件・該当なしなら
/// 出さず幅も取らない（UI 密度の原則）。ツリーの見出し・ファイル一覧の行とパンくず・Git パネルの見出しが<b>同じ
/// インスタンス</b>を見る（<see cref="GitRepositoryChips"/> がリポジトリごとに1つ持つ）。
/// </summary>
public sealed partial class RepositoryChipViewModel : ObservableObject
{
    private readonly RepositoryActions? _actions;

    public RepositoryChipViewModel(string root, RepositoryActions? actions)
    {
        Root = root;
        Name = WorkspacePaths.FolderName(root);
        _actions = actions;
    }

    /// <summary>作業ツリーの根。</summary>
    public string Root { get; }

    /// <summary>見出し名（根のフォルダー名）。</summary>
    public string Name { get; }

    /// <summary>状態を一度でも読めたか。読めるまでは印を出さない（「main」を仮に出して嘘をつかない）。</summary>
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _branch = "";
    [ObservableProperty] private string _aheadText = "";
    [ObservableProperty] private string _behindText = "";
    [ObservableProperty] private string _changesText = "";
    [ObservableProperty] private string _conflictText = "";
    [ObservableProperty] private string _operationText = "";
    [ObservableProperty] private string _toolTip = "";
    [ObservableProperty] private bool _hasChanges;
    /// <summary>fetch／pull の実行中。</summary>
    [ObservableProperty] private bool _isBusy;
    /// <summary>直前の fetch／pull の失敗（成功したら空）。</summary>
    [ObservableProperty] private string _lastError = "";

    /// <summary>状態から表示を作り直す（UI スレッドで呼ぶ）。</summary>
    public void Update(GitRepositoryFileState state)
    {
        var view = Describe(state);
        IsLoaded = state.Loaded;
        Branch = view.Branch;
        AheadText = view.Ahead;
        BehindText = view.Behind;
        ChangesText = view.Changes;
        ConflictText = view.Conflicts;
        OperationText = view.Operation;
        HasChanges = state.ChangedCount > 0;
        ToolTip = view.ToolTip;
    }

    internal readonly record struct ChipText(
        string Branch, string Ahead, string Behind, string Changes, string Conflicts, string Operation, string ToolTip);

    /// <summary>表示の文字（純関数・テスト用に internal）。</summary>
    internal static ChipText Describe(GitRepositoryFileState state)
    {
        var branch = state.Branch.Length == 0 ? "?" : state.Branch;
        var operation = state.Operation switch
        {
            GitRepositoryOperation.Rebase => "REBASING",
            GitRepositoryOperation.Merge => "MERGING",
            GitRepositoryOperation.CherryPick => "CHERRY-PICKING",
            GitRepositoryOperation.Revert => "REVERTING",
            GitRepositoryOperation.Bisect => "BISECTING",
            _ => "",
        };
        var lines = new List<string> { $"{WorkspacePaths.FolderName(state.Root)}  ⎇ {branch}" };
        if (state.Upstream is { } upstream)
            lines.Add(state.UpstreamGone ? $"上流 {upstream}（消えています）" : $"上流 {upstream}" +
                (state.Ahead > 0 || state.Behind > 0 ? $"　↑{state.Ahead} 送っていない ↓{state.Behind} 取り込んでいない" : "　同期済み"));
        else if (!state.IsDetached)
            lines.Add("上流なし");
        if (state.ChangedCount > 0)
            lines.Add($"変更 {state.ChangedCount} 件（ステージ済み {state.StagedCount}・未ステージ {state.UnstagedCount}・未追跡 {state.UntrackedCount}）");
        if (state.ConflictCount > 0)
            lines.Add($"競合 {state.ConflictCount} 件");
        if (operation.Length > 0)
            lines.Add($"{operation} の途中です");
        lines.Add(state.Root);

        return new ChipText(
            branch,
            state.Ahead > 0 ? $"↑{state.Ahead}" : "",
            state.Behind > 0 ? $"↓{state.Behind}" : "",
            state.ChangedCount > 0 ? $"±{state.ChangedCount}" : "",
            state.ConflictCount > 0 ? $"⚠{state.ConflictCount}" : "",
            operation,
            string.Join(Environment.NewLine, lines));
    }

    [RelayCommand]
    private Task SwitchBranch() => RunAsync(RepositoryActionKind.SwitchBranch);

    [RelayCommand]
    private Task Sync() => RunAsync(BehindText.Length > 0 ? RepositoryActionKind.Pull : RepositoryActionKind.Fetch);

    [RelayCommand]
    private Task Fetch() => RunAsync(RepositoryActionKind.Fetch);

    [RelayCommand]
    private Task Pull() => RunAsync(RepositoryActionKind.Pull);

    [RelayCommand]
    private Task OpenChanges() => RunAsync(RepositoryActionKind.OpenChanges);

    [RelayCommand]
    private Task OpenLog() => RunAsync(RepositoryActionKind.OpenLog);

    [RelayCommand]
    private Task OpenTerminal() => RunAsync(RepositoryActionKind.OpenTerminal);

    [RelayCommand]
    private Task OpenInFiles() => RunAsync(RepositoryActionKind.OpenInFiles);

    private async Task RunAsync(RepositoryActionKind kind)
    {
        if (_actions is null || IsBusy) return;
        var remote = kind is RepositoryActionKind.Fetch or RepositoryActionKind.Pull;
        if (remote) IsBusy = true;
        try
        {
            var result = await _actions.ExecuteAsync(Root, kind);
            if (result is not null)
                LastError = result.Success ? "" : (result.Error.Length > 0 ? result.Error.Trim() : "失敗しました");
        }
        finally
        {
            if (remote) IsBusy = false;
        }
    }
}

/// <summary>
/// リポジトリの印をリポジトリごとに1つ持つ（§40.7）。<see cref="GitWorkspaceStatus"/> が読み終えるたびに更新し、
/// まだ読んでいないリポジトリは頼まれた時に裏で読む。
/// </summary>
public sealed class GitRepositoryChips
{
    private readonly GitWorkspaceStatus _status;
    private readonly RepositoryActions? _actions;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, RepositoryChipViewModel> _chips = new(StringComparer.OrdinalIgnoreCase);

    public GitRepositoryChips(GitWorkspaceStatus status, RepositoryActions actions)
        : this(status, actions, UiDispatch.Post)
    {
    }

    internal GitRepositoryChips(GitWorkspaceStatus status, RepositoryActions? actions, Action<Action> post)
    {
        _status = status;
        _actions = actions;
        _post = post;
        _status.StatusLoaded += (_, root) => Refresh(root);
        // git ディレクトリが変わった（コミット・fetch）——ツリーが読み直さない場所（ピン留めで隠れている等）でも
        // 印は新しくする。読み込みは Stale を見て必要なときだけ git を起こす。
        _status.RepositoryChanged += (_, root) =>
        {
            bool watched;
            lock (_chips) watched = _chips.ContainsKey(root);
            if (watched) LoadInBackground(root);
        };
    }

    /// <summary><paramref name="repositoryRoot"/> の印（同じリポジトリには同じインスタンス）。未読込なら裏で読む。</summary>
    public RepositoryChipViewModel For(string repositoryRoot)
    {
        var root = WorkspacePaths.Normalize(repositoryRoot);
        RepositoryChipViewModel chip;
        bool created;
        lock (_chips)
        {
            created = !_chips.TryGetValue(root, out chip!);
            if (created)
                _chips[root] = chip = new RepositoryChipViewModel(root, _actions);
        }
        if (created)
        {
            if (_status.Cached(root) is { } cached)
                chip.Update(cached);
            else
                LoadInBackground(root);
        }
        return chip;
    }

    /// <summary>パスがリポジトリの根ならその印、そうでなければ null（ツリー・ファイル一覧の行に添えるか決める）。</summary>
    public RepositoryChipViewModel? ForRootPath(string fullPath)
    {
        var repository = _status.Catalog.RepositoryFor(fullPath);
        return repository is not null
               && string.Equals(repository.Root, WorkspacePaths.Normalize(fullPath), StringComparison.OrdinalIgnoreCase)
            ? For(repository.Root)
            : null;
    }

    /// <summary><see cref="ForRootPath"/> の、作り済みの一覧だけで答える版（UI スレッドから呼んでよい）。</summary>
    public RepositoryChipViewModel? ForKnownRootPath(string fullPath)
    {
        var repository = _status.Catalog.KnownRepositoryFor(fullPath);
        return repository is not null
               && string.Equals(repository.Root, WorkspacePaths.Normalize(fullPath), StringComparison.OrdinalIgnoreCase)
            ? For(repository.Root)
            : null;
    }

    private void Refresh(string root)
    {
        RepositoryChipViewModel? chip;
        lock (_chips) _chips.TryGetValue(root, out chip);
        if (chip is null || _status.Cached(root) is not { } state) return;
        _post(() => chip.Update(state));
    }

    private void LoadInBackground(string root)
        => _ = Task.Run(() =>
        {
            try { _status.Load(root, fresh: false, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
        });
}
