using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>サイドバー Git パネルの1変更行。</summary>
public sealed partial class GitChangeItem : ObservableObject
{
    public GitChangeItem(GitChangeEntry entry, bool isStaged, bool isChecked = false)
    {
        Entry = entry;
        IsStaged = isStaged;
        FileName = Path.GetFileName(entry.Path);
        Directory = Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? "";
        Status = entry.IsConflicted ? "U"
            : entry.IsUntracked ? "?"
            : (isStaged ? entry.IndexStatus : entry.WorkStatus).ToString();
        _isChecked = isChecked;
    }

    public GitChangeEntry Entry { get; }
    public bool IsStaged { get; }
    public string FileName { get; }
    public string Directory { get; }
    public string Status { get; }
    /// <summary>作業ツリーのツリー表示でコミット対象に含めるか。ステージ済みには使わない。</summary>
    [ObservableProperty] private bool _isChecked;
    public bool IsConflicted => Entry.IsConflicted;

    /// <summary>増減の行数（<c>+12 −3</c>）。数えていない（未追跡など）なら null で、行には何も出さない（§24.32）。</summary>
    [ObservableProperty] private GitLineStat? _lineStat;
    public string ToolTipText => Entry.OrigPath is null
        ? Entry.Path
        : $"{Entry.OrigPath} → {Entry.Path}";
}

/// <summary>ディレクトリ階層と親子連動チェックを持つGit変更ノード。</summary>
public sealed class GitChangeTreeNode : ObservableObject
{
    private bool? _isChecked;
    private bool _isExpanded = true;

    private GitChangeTreeNode(string name, GitChangeTreeNode? parent, GitChangeItem? change)
    {
        Name = name;
        Parent = parent;
        Change = change;
        _isChecked = change?.IsChecked ?? false;
    }

    public string Name { get; private set; }
    public GitChangeTreeNode? Parent { get; private set; }
    public GitChangeItem? Change { get; }
    public bool IsDirectory => Change is null;
    public bool IsSection { get; private set; }
    public int LeafCount { get; private set; }
    public ObservableCollection<GitChangeTreeNode> Children { get; } = new();

    /// <summary>展開中か。ディレクトリ／セクション行のツリーアイテムと TwoWay で結ぶ。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            // 三状態は表示専用。クリック操作は「全選択／全解除」の2状態で循環させる。
            var requested = value is null && _isChecked.HasValue ? !_isChecked.Value : value;
            SetChecked(requested, updateChildren: true, updateParent: true);
        }
    }

    public static GitChangeTreeNode Build(IEnumerable<GitChangeItem> items)
    {
        var root = new GitChangeTreeNode("", null, null);
        foreach (var item in items.OrderBy(i => i.Entry.Path, StringComparer.OrdinalIgnoreCase))
            root.Add(item);
        root.CompactAndSort();
        root.Recalculate();
        return root;
    }

    public static GitChangeTreeNode BuildSection(string name, IEnumerable<GitChangeItem> items, bool expanded = true)
    {
        var materialized = items.ToArray();
        var root = Build(materialized);
        root.Name = name;
        root.IsSection = true;
        root.LeafCount = materialized.Length;
        root.IsExpanded = expanded;
        return root;
    }

    private void Add(GitChangeItem item)
    {
        var parts = item.Entry.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = this;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var name = parts[i];
            var next = current.Children.FirstOrDefault(n => n.IsDirectory
                && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                next = new GitChangeTreeNode(name, current, null);
                current.Children.Add(next);
            }
            current = next;
        }
        current.Children.Add(new GitChangeTreeNode(parts.LastOrDefault() ?? item.FileName, current, item));
    }

    /// <summary>子がディレクトリ1つだけの階層を src/Loomo.App のようにまとめ、縦長化を防ぐ。</summary>
    private void CompactAndSort()
    {
        foreach (var child in Children.ToArray()) child.CompactAndSort();
        if (Parent is not null)
        {
            while (Children.Count == 1 && Children[0].IsDirectory)
            {
                var only = Children[0];
                Name += "/" + only.Name;
                Children.Clear();
                foreach (var grandChild in only.Children)
                {
                    grandChild.Parent = this;
                    Children.Add(grandChild);
                }
            }
        }
        var ordered = Children.OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Children.Clear();
        foreach (var child in ordered) Children.Add(child);
    }

    private void Recalculate()
    {
        foreach (var child in Children) child.Recalculate();
        UpdateFromChildren();
    }

    private void UpdateFromChildren()
    {
        if (Children.Count == 0) return;
        var first = Children[0].IsChecked;
        SetChecked(Children.All(c => c.IsChecked == first) ? first : null, false, true);
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        // CallerMemberName に頼ると "SetChecked" が通知名になり、IsChecked にバインドした
        // 子・親のチェックボックスへ届かない（連動が見た目に反映されない）ため明示する。
        if (!SetProperty(ref _isChecked, value, nameof(IsChecked))) return;
        if (value.HasValue)
        {
            if (Change is not null) Change.IsChecked = value.Value;
            if (updateChildren)
                foreach (var child in Children)
                    child.SetChecked(value, true, false);
        }
        if (updateParent) Parent?.UpdateFromChildren();
    }
}

/// <summary>サイドバーの amend 対象として選べるコミット。</summary>
public sealed record GitAmendCommitOption(string Hash, string ShortHash, string Subject, string Date)
{
    public string DisplayText => $"{ShortHash}  {Subject}  ({Date})";
}

/// <summary>
/// サイドバー Git パネルの ViewModel。変更一覧・ステージ操作・コミット・push/pull/fetch を扱う。
/// 複雑な操作（ログツリー・rebase 等）は Git セッションペイン（<see cref="GitSessionViewModel"/>）が担う。
/// </summary>
public sealed partial class GitPanelViewModel : ObservableObject
{
    private readonly GitService _git;
    private readonly IEditorService _editor;
    private readonly IWorkspaceService _workspace;

    /// <summary>「差分を開く」操作：何の差分かだけを渡して表示を要求する（Diff ペインへ出すか、
    /// ペインが隠れていれば別ウィンドウで開くかは ShellWindow が決める）。</summary>
    public event EventHandler<DiffOpenTarget>? DiffOpenRequested;

    /// <summary>初回の読込を済ませたか。Git パネルを開くまで遅延する。</summary>
    private bool _loaded;

    [ObservableProperty] private bool _isRepository = true;
    [ObservableProperty] private string _branchLabel = "";

    /// <summary>分岐元へマージしたら衝突しそうなファイル（§24.40）。null なら衝突しない（か、まだ見ていない）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictForecastLabel), nameof(ConflictForecastToolTip), nameof(HasConflictForecast))]
    private GitConflictForecast? _conflictForecast;

    public bool HasConflictForecast => ConflictForecast is { HasConflicts: true };

    public string ConflictForecastLabel => ConflictForecast is { HasConflicts: true } f
        ? $"⚠ {ShortRef(f.Target)} と衝突 {f.Files.Count}" : "";

    public string? ConflictForecastToolTip => ConflictForecast is { HasConflicts: true } f
        ? $"このブランチを {f.Target} へマージすると衝突するファイル（作業ツリーには触らずに確かめた結果）:\n"
          + string.Join("\n", f.Files.Take(10).Select(path => "  " + path))
          + (f.Files.Count > 10 ? $"\n  ほか {f.Files.Count - 10} ファイル" : "")
          + "\n押すと一覧を開く"
        : null;

    private static string ShortRef(string target) => target.StartsWith("origin/", StringComparison.Ordinal) ? target[7..] : target;

    /// <summary>衝突しそうなファイルの一覧を開いてほしい（ShellWindow がパレットで出す）。</summary>
    public event Action<GitConflictForecast>? ConflictForecastOpenRequested;

    [RelayCommand]
    private void OpenConflictForecast()
    {
        if (ConflictForecast is { HasConflicts: true } forecast)
            ConflictForecastOpenRequested?.Invoke(forecast);
    }

    private int _forecastGeneration;

    /// <summary>
    /// 分岐元（比較基準がブランチ／分岐点ならそのブランチ、作業ツリーなら既定のブランチ）とぶつかるかを確かめ直す（§24.40）。
    /// HEAD と相手のコミットが前回と同じなら git に聞き直さない（状態の更新は保存のたびに走る）。
    /// 一覧を出したあとに回すので、遅い merge-tree でも一覧は待たせない。
    /// </summary>
    private async Task UpdateConflictForecastAsync()
    {
        var generation = ++_forecastGeneration;
        string? target = CompareBase.NeedsBranch ? CompareBase.Selection.Target : null;
        target ??= await _git.GetDefaultBranchAsync();
        if (generation != _forecastGeneration) return;
        if (string.IsNullOrWhiteSpace(target) || string.Equals(target, BranchLabel, StringComparison.Ordinal))
        {
            ConflictForecast = null;
            return;
        }
        var forecast = await _git.ForecastConflictsAsync(target);
        if (generation != _forecastGeneration) return;
        // 同じ答え（サービスが前回の答えをそのまま返した）なら触らない——ツールチップや一覧を作り直さない。
        if (!ReferenceEquals(ConflictForecast, forecast))
            ConflictForecast = forecast.Error is null ? forecast : null;
    }
    /// <summary>上流より進んでいるコミット数（プッシュ対象）。0 なら送るものなし。</summary>
    [ObservableProperty] private int _ahead;
    /// <summary>上流より遅れているコミット数（プル対象）。0 なら取り込むものなし。</summary>
    [ObservableProperty] private int _behind;
    [ObservableProperty] private bool _hasRemote;
    [ObservableProperty] private bool _hasUpstream;
    [ObservableProperty] private string _commitMessage = "";
    /// <summary>書きかけのメッセージの帳面（対象フォルダーごと・ワークスペースに保存・§24.32）。</summary>
    private readonly CommitDraftBook _commitDrafts = new();
    /// <summary>いまメッセージ欄が誰の下書きを映しているか（Git の対象フォルダー）。</summary>
    private string? _commitDraftRoot;
    /// <summary>amend を立てる前に書いていたメッセージ。amend の間は欄が対象コミットの件名になるので、
    /// 下ろしたときにこれを戻す（以前は空にしていた＝書きかけが消えた）。</summary>
    private string _messageBeforeAmend = "";
    [ObservableProperty] private bool _amend;
    private Task? _amendCommitsLoadTask;
    private bool _amendCommitsLoaded;
    private int _amendCommitsGeneration;
    /// <summary>amend が有効なときだけ表示する、現在ブランチの主系列コミット。</summary>
    public ObservableCollection<GitAmendCommitOption> AmendCommits { get; } = new();
    [ObservableProperty] private GitAmendCommitOption? _selectedAmendCommit;
    /// <summary>true なら次のコミットに <c>-S</c>（GPG署名）を付ける。署名鍵未設定・gpg 未インストール等の
    /// 失敗は通常の失敗時と同じく RunOpAsync が git のエラー出力をそのまま表示する。</summary>
    [ObservableProperty] private bool _sign;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _statusIsError;

    /// <summary>ステージ済みの変更。コミット時はこれがそのまま入る（チェックボックスは持たない）。</summary>
    public ObservableCollection<GitChangeItem> Staged { get; } = new();
    /// <summary>追跡済みの未ステージ変更。チェックでコミット対象に含める。</summary>
    public ObservableCollection<GitChangeItem> Changes { get; } = new();
    /// <summary>Git の管理対象になっていないファイル。チェックでコミット対象に含める。</summary>
    public ObservableCollection<GitChangeItem> UnversionedFiles { get; } = new();
    /// <summary>「変更」「バージョン管理外」をトップレベルに持つ、コミット対象選択用のツリー。
    /// 基準がブランチ／分岐点のときは、その基準に対する変更ファイルの単一セクションになる。</summary>
    [ObservableProperty] private IReadOnlyList<GitChangeTreeNode> _workingTreeSections = Array.Empty<GitChangeTreeNode>();

    /// <summary>
    /// 今の比較基準で意味を持つ操作。ステージ／アンステージ／破棄・コミット対象チェック・コミットは
    /// 「作業ツリー vs インデックス／HEAD」の概念で、<c>main</c> との比較には存在しない——
    /// ビューはこの各フラグでそれらを<b>出す／出さない</b>を決める（無効化して押せるのに何も
    /// 起きない項目にしない）。コマンド側の二重ガードも同じフラグを見るので、判定は
    /// <see cref="GitCompareCapabilities.For(GitCompareBaseKind)"/> 一箇所に集まる。
    /// ファイルを開く・差分を開くは基準に依らないので、ここには現れない（常に出す）。
    /// </summary>
    [ObservableProperty]
    private GitCompareCapabilities _capabilities =
        GitCompareCapabilities.For(GitCompareBaseKind.WorkingTree);

    /// <summary>基準に対する変更一覧そのものが取れなかった理由（曖昧な引数・壊れた ref など）。
    /// 空なら問題なし。<b>基準を解決できなかった理由は別</b>で、そちらは基準選択 UI
    /// （<see cref="GitCompareBaseViewModel.ErrorMessage"/>）が出す——同じ理由を2箇所に出さない。</summary>
    [ObservableProperty] private string _compareBaseError = "";

    /// <summary>退避中のスタッシュ（新しい＝stash@{0} が先頭）。</summary>
    public ObservableCollection<GitStashEntry> Stashes { get; } = new();

    // ステージ済みリストの選択スナップショット（コードビハインドの SelectionChanged から差し替える）。
    // 一括アンステージはここを対象にするので、コマンド側は引数を取らない。
    private readonly List<GitChangeItem> _stagedSelection = new();

    /// <summary>ステージ済みリストで選択中の件数（選択件数バーの表示・有効化に使う）。</summary>
    [ObservableProperty] private int _stagedSelectedCount;

    /// <summary>ステージ済みリストの選択をビューから受け取る。</summary>
    public void SetStagedSelection(IList items)
    {
        _stagedSelection.Clear();
        foreach (var o in items)
            if (o is GitChangeItem g) _stagedSelection.Add(g);
        StagedSelectedCount = _stagedSelection.Count;
    }

    /// <summary>Git 操作の対象フォルダーの切替 UI 状態。サイドバー Git パネルとセッションペイン
    /// （<see cref="GitSessionViewModel"/>）で共有する（どちらから切り替えても両方に反映される）。</summary>
    public GitRootSwitchViewModel RootSwitch { get; }

    /// <summary>比較基準（作業ツリー／ブランチ／分岐点）。Diff ペインと共有する Singleton。</summary>
    public GitCompareBaseViewModel CompareBase { get; }

    public GitPanelViewModel(
        GitService git, IEditorService editor, IWorkspaceService workspace,
        GitRootSwitchViewModel rootSwitch, GitCompareBaseViewModel compareBase)
    {
        _git = git;
        _editor = editor;
        _workspace = workspace;
        RootSwitch = rootSwitch;
        CompareBase = compareBase;
        // 基準が変わったら一覧を組み直す（ポーリング署名は git status しか見ないので、
        // 基準の切替はそこには現れない＝自前で読み直さないと古い一覧が残る）。
        CompareBase.Changed += OnCompareBaseChanged;
        _git.RepositoryChanged += OnRepositoryChanged;
        _git.ActiveRootChanged += OnActiveRootChanged;
        Staged.CollectionChanged += OnCommitCandidatesChanged;
        Changes.CollectionChanged += OnCommitCandidatesChanged;
        UnversionedFiles.CollectionChanged += OnCommitCandidatesChanged;
    }

    private bool CanCommit() =>
        // 基準がブランチ／分岐点のときはコミット欄ごと出さないが、amend を立てたまま基準を切り替えても
        // true のままにならないよう、判定にも同じフラグを入れる。
        Capabilities.CanCommit && IsRepository && !IsBusy && !string.IsNullOrWhiteSpace(CommitMessage)
        && (!Amend || SelectedAmendCommit is not null)
        && (Amend || Staged.Count > 0 || Changes.Any(i => i.IsChecked) || UnversionedFiles.Any(i => i.IsChecked));

    private void OnCommitCandidatesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (GitChangeItem item in e.OldItems)
                item.PropertyChanged -= OnCommitCandidatePropertyChanged;
        if (e.NewItems is not null)
            foreach (GitChangeItem item in e.NewItems)
                item.PropertyChanged += OnCommitCandidatePropertyChanged;
        CommitCommand.NotifyCanExecuteChanged();
    }

    private void OnCommitCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitChangeItem.IsChecked))
            CommitCommand.NotifyCanExecuteChanged();
    }

    partial void OnCapabilitiesChanged(GitCompareCapabilities value) =>
        CommitCommand.NotifyCanExecuteChanged();

    partial void OnCommitMessageChanged(string value) => CommitCommand.NotifyCanExecuteChanged();
    partial void OnAmendChanged(bool value)
    {
        CommitCommand.NotifyCanExecuteChanged();
        if (!value)
        {
            _amendCommitsGeneration++;
            SelectedAmendCommit = null;
            AmendCommits.Clear();
            CommitMessage = _messageBeforeAmend;
            _messageBeforeAmend = "";
            return;
        }
        _messageBeforeAmend = CommitMessage;
        _amendCommitsLoaded = false;
        RequestAmendCommitsLoad();
    }

    partial void OnSelectedAmendCommitChanged(GitAmendCommitOption? value)
    {
        CommitCommand.NotifyCanExecuteChanged();
        // amend は元コミットのメッセージを --no-edit / fixup で保持するため、
        // 選択変更のたびに git show を実行する必要はない。件名だけを表示する。
        CommitMessage = value?.Subject ?? "";
    }

    /// <summary>amend 候補の読み直しを試す上限。1回目は走っている読み込みに相乗りしうるので複数回。</summary>
    private const int MaxAmendCommitsLoadAttempts = 3;

    private void RequestAmendCommitsLoad()
    {
        if (_amendCommitsLoadTask is { IsCompleted: false }) return;
        _amendCommitsLoadTask = LoadAmendCommitsAsync(_amendCommitsGeneration);
        _ = _amendCommitsLoadTask;
    }

    /// <summary>
    /// amend 候補が読めていなければ読む。履歴は pull・チェックアウト・別ペインからのコミットでも
    /// 動くので、その通知（<see cref="OnRepositoryChanged"/>／<see cref="OnActiveRootChanged"/>）で
    /// 読み込み済みフラグを落として引き直す——一度きりにすると、amend にチェックを入れたままの
    /// 一覧が古いブランチのハッシュを指したまま残る。
    /// </summary>
    private async Task EnsureAmendCommitsLoadedAsync()
    {
        // 割り込み（世代更新）や、走っていた古い読み込みの取りこぼしを拾い直すが、回数は必ず
        // 頭打ちにする。「読み込み済みになるまで回す」にすると、git log が失敗したときに
        // フラグが立たないまま——しかも GetLogAsync は失敗した Task も返すので待機すら
        // 発生せず——UI スレッド上で回り続け、アプリが固まる。
        for (var attempt = 0; attempt < MaxAmendCommitsLoadAttempts && Amend && !_amendCommitsLoaded; attempt++)
        {
            RequestAmendCommitsLoad();
            if (_amendCommitsLoadTask is { } load)
                await load;
        }
    }

    private async Task LoadAmendCommitsAsync(int generation)
    {
        IReadOnlyList<GitLogRow> rows;
        try
        {
            rows = await _git.GetLogAsync(new GitLogQuery
            {
                BranchRef = "HEAD",
                FirstParent = true,
                Limit = 300,
            });
        }
        catch (Exception ex)
        {
            ReportGitException("amend対象の履歴取得", ex);
            return;
        }
        if (!Amend || generation != _amendCommitsGeneration) return;

        var options = rows.Where(row => row.IsCommit && row.Hash is not null)
            .Select(row => new GitAmendCommitOption(
                row.Hash!, row.ShortHash ?? row.Hash![..Math.Min(8, row.Hash!.Length)],
                string.IsNullOrWhiteSpace(row.Subject) ? "（メッセージなし）" : row.Subject,
                row.Date ?? ""))
            .ToList();
        _amendCommitsLoaded = true;
        // 履歴が動いていない更新（作業ツリーを触っただけ）で一覧を作り直すと、開いている
        // ドロップダウンや選択が毎回リセットされる。中身が同じなら何もしない。
        if (AmendCommits.SequenceEqual(options)) return;

        var selectedHash = SelectedAmendCommit?.Hash;
        AmendCommits.Clear();
        foreach (var option in options)
            AmendCommits.Add(option);

        SelectedAmendCommit = AmendCommits.FirstOrDefault(c =>
            string.Equals(c.Hash, selectedHash, StringComparison.OrdinalIgnoreCase))
            ?? AmendCommits.FirstOrDefault();
        if (SelectedAmendCommit is null)
            CommitMessage = "";
    }
    /// <summary>
    /// 同期系コマンドの可否をまとめて再評価する。プル／プッシュは<b>方式ごとに別コマンド</b>
    /// （通常・リベース・早送りのみ／通常・強制）なので、条件が変わる場所ごとに列挙していると
    /// 増やしたときに必ず1つ書き漏らす——通知先はここ1箇所に持つ。
    /// </summary>
    private void NotifySyncCommands()
    {
        FetchCommand.NotifyCanExecuteChanged();
        PullCommand.NotifyCanExecuteChanged();
        PullMergeCommand.NotifyCanExecuteChanged();
        PullRebaseCommand.NotifyCanExecuteChanged();
        PullFastForwardCommand.NotifyCanExecuteChanged();
        PushCommand.NotifyCanExecuteChanged();
        PushForceCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value)
    {
        CommitCommand.NotifyCanExecuteChanged();
        CloneCommand.NotifyCanExecuteChanged();
        NotifySyncCommands();
    }
    partial void OnIsRepositoryChanged(bool value)
    {
        CommitCommand.NotifyCanExecuteChanged();
        NotifySyncCommands();
    }
    partial void OnHasRemoteChanged(bool value) => NotifySyncCommands();
    partial void OnHasUpstreamChanged(bool value) => NotifySyncCommands();
    partial void OnAheadChanged(int value) => NotifySyncCommands();
    partial void OnBehindChanged(int value) => NotifySyncCommands();
    private IDisposable? _live;

    /// <summary>Git パネルが見えている間のライブ監視を開始する（手動更新ボタンの代わり）。
    /// 開いた瞬間に最新化し、以降は GitService の軽量ポーリングが変化を取り込む。</summary>
    public void StartLiveTracking()
    {
        if (_live is not null) return;
        _git.InvalidateReadCache();
        _live = _git.TrackLiveChanges();
        _ = RefreshAsync();
    }

    /// <summary>Git パネルが隠れたらライブ監視を止める（見えていない間はチェックしない）。</summary>
    public void StopLiveTracking()
    {
        _live?.Dispose();
        _live = null;
    }

    private void OnRepositoryChanged(object? sender, EventArgs e)
    {
        _amendCommitsLoaded = false;
        _amendCommitsGeneration++;
        if (!_loaded) return;
        // 更新系コマンドの完了スレッドは UI とは限らないためディスパッチする
        UiDispatch.Post(() => _ = RefreshAsync());
    }

    private void OnActiveRootChanged(object? sender, EventArgs e)
    {
        UiDispatch.Post(SwitchCommitDraftToActiveRoot);
        _amendCommitsLoaded = false;
        _amendCommitsGeneration++;
        if (!_loaded) return;
        UiDispatch.Post(() => _ = RefreshAsync());
    }

    /// <summary>いまの欄の下書き（amend 中は amend を立てる前に書いていたもの）。</summary>
    private string CurrentDraftText => Amend ? _messageBeforeAmend : CommitMessage;

    /// <summary>対象フォルダーが替わったら、前の下書きを帳面へしまい、新しい対象の下書きを欄へ出す。
    /// 同じ対象のままなら何もしない（復元の直後に遅れて届いた通知が、戻した下書きを上書きしない）。</summary>
    private void SwitchCommitDraftToActiveRoot()
    {
        var root = _git.RootPath;
        if (string.Equals(root, _commitDraftRoot, StringComparison.OrdinalIgnoreCase)) return;
        _commitDrafts.Put(_commitDraftRoot, CurrentDraftText);
        _commitDraftRoot = root;
        ShowDraft(_commitDrafts.Get(root));
    }

    private void ShowDraft(string text)
    {
        if (Amend)
        {
            // amend を下ろすと _messageBeforeAmend が欄へ戻るので、先に入れておく。
            _messageBeforeAmend = text;
            Amend = false;
        }
        else
        {
            CommitMessage = text;
        }
    }

    /// <summary>ワークスペースの保存：いまの欄も含めた下書きの帳面（無ければ null）。</summary>
    public Dictionary<string, string>? CaptureCommitDrafts()
    {
        _commitDrafts.Put(_commitDraftRoot ?? _git.RootPath, CurrentDraftText);
        return _commitDrafts.Export();
    }

    /// <summary>ワークスペースの復元：帳面を差し替え、いまの対象フォルダーの下書きを欄へ出す。
    /// Git の対象フォルダーが切り替わったあと（LoadRoot の後）に呼ぶ。</summary>
    public void RestoreCommitDrafts(IReadOnlyDictionary<string, string>? drafts)
    {
        _commitDrafts.Replace(drafts);
        _commitDraftRoot = _git.RootPath;
        ShowDraft(_commitDrafts.Get(_commitDraftRoot));
    }

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshSafeAsync();

    private async Task RefreshSafeAsync()
    {
        try
        {
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            ReportGitException("Git状態の更新", ex);
        }
    }

    private async Task RefreshCoreAsync()
    {
        _loaded = true;
        var status = await _git.GetStatusAsync();

        IsRepository = status.IsRepository;
        if (!status.IsRepository)
        {
            BranchLabel = "";
            Ahead = 0;
            Behind = 0;
            HasRemote = false;
            HasUpstream = false;
            PullPreview = null;
            Staged.Clear();
            Changes.Clear();
            UnversionedFiles.Clear();
            WorkingTreeSections = Array.Empty<GitChangeTreeNode>();
            Stashes.Clear();
            CompareBaseError = "";
            Capabilities = GitCompareCapabilities.For(GitCompareBaseKind.WorkingTree);
            return;
        }

        BranchLabel = status.Branch;
        Ahead = status.Ahead;
        Behind = status.Behind;
        HasUpstream = !string.IsNullOrWhiteSpace(status.Upstream);
        _upstream = status.Upstream;
        // 出している確認が今の状態と合わなくなった（取り込んだ・フェッチで数が変わった）ら下げる。
        if (PullPreview is { } preview
            && (preview.Incoming != Behind || preview.Outgoing != Ahead || preview.Upstream != status.Upstream))
            PullPreview = null;
        HasRemote = (await _git.GetRemotesAsync()).Count > 0;

        // スタッシュは基準に依らず作業ツリーの退避なので、どちらの基準でも同じように出す。
        var stashEntries = await _git.GetStashesAsync();
        Stashes.Clear();
        foreach (var stash in stashEntries)
            Stashes.Add(stash);

        // 比較基準を解決する。作業ツリー基準なら git は1回も起動しない（従来経路の負荷は変えない）。
        var resolution = await CompareBase.ResolveAsync();
        Capabilities = CompareBase.Capabilities;
        CompareBaseError = "";
        if (!CompareBase.IsWorkingTree)
        {
            await LoadCompareBaseChangesAsync(resolution);
            return;
        }

        // Amend を直後に ON にしたケースでも、候補のロード完了を待ってから更新を返す。
        await EnsureAmendCommitsLoadedAsync();

        // ライブ更新でチェックが勝手に戻らないよう、作業ツリー側はパス単位で選択状態を引き継ぐ。
        var wasChecked = Changes.Concat(UnversionedFiles)
            .Where(i => i.IsChecked).Select(i => i.Entry.Path)
            .ToHashSet(StringComparer.Ordinal);

        Staged.Clear();
        foreach (var entry in status.Staged)
            Staged.Add(new GitChangeItem(entry, isStaged: true));

        Changes.Clear();
        UnversionedFiles.Clear();
        foreach (var entry in status.Unstaged)
        {
            var item = new GitChangeItem(entry, isStaged: false, isChecked: wasChecked.Contains(entry.Path));
            (entry.IsUntracked ? UnversionedFiles : Changes).Add(item);
        }

        // セクションの開閉はユーザー操作を更新間で引き継ぐ。「バージョン管理外ファイル」は既定で畳む。
        var wasExpanded = WorkingTreeSections.ToDictionary(s => s.Name, s => s.IsExpanded, StringComparer.Ordinal);
        var sections = new List<GitChangeTreeNode>(2);
        if (Changes.Count > 0)
            sections.Add(GitChangeTreeNode.BuildSection(
                "変更", Changes, wasExpanded.GetValueOrDefault("変更", true)));
        if (UnversionedFiles.Count > 0)
            sections.Add(GitChangeTreeNode.BuildSection(
                "バージョン管理外ファイル", UnversionedFiles, wasExpanded.GetValueOrDefault("バージョン管理外ファイル", false)));
        WorkingTreeSections = sections;
        await ApplyLineStatsAsync(baseRef: null);
        await UpdateConflictForecastAsync();
    }

    /// <summary>各行に増減の行数を添える（§24.32）。一覧を出してから数えるので、遅い git でも一覧は待たせない。
    /// ステージ済みはインデックス側、未ステージは作業ツリー側、基準があればその基準との差を数える。</summary>
    private async Task ApplyLineStatsAsync(string? baseRef)
    {
        if (baseRef is not null)
        {
            var against = await _git.GetLineStatsAsync(cached: false, baseRef);
            foreach (var node in WorkingTreeSections)
                foreach (var item in Leaves(node))
                    item.LineStat = against.GetValueOrDefault(item.Entry.Path);
            return;
        }
        var staged = Staged.Count > 0
            ? await _git.GetLineStatsAsync(cached: true)
            : new Dictionary<string, GitLineStat>();
        var unstaged = Changes.Count > 0
            ? await _git.GetLineStatsAsync(cached: false)
            : new Dictionary<string, GitLineStat>();
        foreach (var item in Staged)
            item.LineStat = staged.GetValueOrDefault(item.Entry.Path);
        foreach (var item in Changes)
            item.LineStat = unstaged.GetValueOrDefault(item.Entry.Path);
    }

    private static IEnumerable<GitChangeItem> Leaves(GitChangeTreeNode node)
    {
        if (node.Change is { } change) yield return change;
        foreach (var child in node.Children)
            foreach (var leaf in Leaves(child))
                yield return leaf;
    }

    /// <summary>
    /// ブランチ／分岐点基準の変更ファイル一覧を組み立てる。<c>git diff --name-status &lt;base&gt;</c>
    /// （二点記法＝未コミットの編集も含む）なので、<b>未追跡ファイルは出ない</b>——まだ git に足していない
    /// ファイルは「このブランチが基準に対して入れた変更」ではないため。リネームは <c>R</c> の1行にまとまる。
    /// ステージ済み・コミット対象チェックはこの基準では意味を持たないので、集合ごと空にする。
    /// </summary>
    private async Task LoadCompareBaseChangesAsync(GitCompareResolution resolution)
    {
        Staged.Clear();
        Changes.Clear();
        UnversionedFiles.Clear();

        if (resolution.BaseRef is null)
        {
            // 解決できていない（理由は CompareBaseError に出ている）。空一覧のままにする。
            WorkingTreeSections = Array.Empty<GitChangeTreeNode>();
            return;
        }

        var changes = await _git.GetCompareChangesAsync(resolution.BaseRef);
        if (changes.HasError)
        {
            // 一覧が取れないのに黙って空を出すと「差分があるのに無い」と嘘をつく。理由を出す。
            CompareBaseError = changes.Error!;
            WorkingTreeSections = Array.Empty<GitChangeTreeNode>();
            return;
        }

        var items = changes.Files
            .Select(change => new GitChangeItem(
                new GitChangeEntry(change.Path, change.OrigPath, ' ', change.Status,
                    IsUntracked: false, IsConflicted: false),
                isStaged: false))
            .ToList();
        WorkingTreeSections = items.Count == 0
            ? Array.Empty<GitChangeTreeNode>()
            : new[] { GitChangeTreeNode.BuildSection(resolution.Label, items) };
        if (items.Count > 0)
            await ApplyLineStatsAsync(resolution.BaseRef);
        await UpdateConflictForecastAsync();
    }

    private void OnCompareBaseChanged(object? sender, EventArgs e)
    {
        // まだ開いていない（＝一度も読んでいない）パネルでは読まない。表示された時点の
        // StartLiveTracking が最新の基準で読み直すので、隠れている間に git を叩く必要はない。
        if (!_loaded) return;
        UiDispatch.Post(() => _ = RefreshAsync());
    }

    /// <summary>現在の変更を退避する（任意のメッセージ付き・未追跡ファイルも含める）。</summary>
    [RelayCommand]
    private async Task StashPushAsync()
    {
        var message = Views.InputDialog.Prompt(
            Application.Current?.MainWindow, "スタッシュ", "メッセージ（任意）:", allowEmpty: true);
        if (message is null) return; // キャンセル
        await RunOpAsync("スタッシュ", () => _git.StashPushAsync(message, includeUntracked: true));
    }

    /// <summary>スタッシュを作業ツリーへ復元する（退避は残す）。</summary>
    [RelayCommand]
    private Task StashApplyAsync(GitStashEntry? stash) => stash is null
        ? Task.CompletedTask
        : RunOpAsync("スタッシュ適用", () => _git.StashApplyAsync(stash.Ref));

    /// <summary>スタッシュを作業ツリーへ復元し、その退避を削除する。</summary>
    [RelayCommand]
    private Task StashPopAsync(GitStashEntry? stash) => stash is null
        ? Task.CompletedTask
        : RunOpAsync("スタッシュ取り出し", () => _git.StashPopAsync(stash.Ref));

    /// <summary>スタッシュを破棄する（復元しない・確認つき）。</summary>
    [RelayCommand]
    private async Task StashDropAsync(GitStashEntry? stash)
    {
        if (stash is null) return;
        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{stash.Ref} を破棄しますか？\n{stash.Description}\n復元できません。",
            "スタッシュの破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        await RunOpAsync("スタッシュ破棄", () => _git.StashDropAsync(stash.Ref));
    }

    /// <summary>スタッシュの中身を差分で見る（適用しない）。退避したときの HEAD → スタッシュの範囲として
    /// Diff ペインへ頼む。未追跡のファイルはこの範囲に出ないので、あれば件数を知らせる。</summary>
    [RelayCommand]
    private async Task OpenStashDiffAsync(GitStashEntry? stash)
    {
        if (stash is null) return;
        var snapshot = await _git.ResolveStashAsync(stash.Ref);
        if (snapshot is null)
        {
            StatusMessage = $"{stash.Ref} が見つかりません。一覧を更新してください。";
            return;
        }
        DiffOpenRequested?.Invoke(this, new DiffOpenTarget.CommitRange(
            snapshot.BaseHash, snapshot.StashHash, $"スタッシュ {stash.Ref}"));
        StatusMessage = snapshot.UntrackedCount > 0
            ? $"{stash.Ref}：未追跡のファイル {snapshot.UntrackedCount} 件は差分に出ません（パッチとして保存すると入ります）。"
            : "";
    }

    /// <summary>スタッシュの中身をパッチとして保存し、エディタで開く（未追跡のファイル・バイナリも含む）。
    /// 保存したパッチは <c>git apply</c> でそのまま当てられる。</summary>
    [RelayCommand]
    private async Task SaveStashPatchAsync(GitStashEntry? stash)
    {
        if (stash is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "スタッシュをパッチとして保存",
            Filter = "パッチ (*.patch)|*.patch|すべてのファイル (*.*)|*.*",
            FileName = stash.PatchFileName,
        };
        if (_git.RootPath is { Length: > 0 } root && Directory.Exists(root))
            dialog.InitialDirectory = root;
        var owner = Application.Current?.MainWindow;
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return;

        var path = dialog.FileName;
        var result = await _git.SaveStashPatchAsync(stash.Ref, path);
        if (!result.Success)
        {
            StatusMessage = $"パッチの保存に失敗しました: {Truncate(result.Message)}";
            return;
        }
        StatusMessage = $"{stash.Ref} を {Path.GetFileName(path)} に保存しました。";
        await _editor.OpenFileAsync(path);
    }

    /// <summary>右クリックした行から、パッチ・移す先に含めるパス（§24.79）。その行がチェックされていればチェックした全部、
    /// そうでなければその行だけ（チェックが残ったまま別の行を右クリックしたときに、見えていない行まで巻き込まない）。
    /// リネームは新旧の両方を含める（新しい側だけだと、古い側の削除が抜けたパッチになる）。</summary>
    internal IReadOnlyList<string> PathsForChangesAction(GitChangeItem? item)
    {
        var picked = item is null || item.IsChecked
            ? Changes.Concat(UnversionedFiles).Where(i => i.IsChecked).ToList()
            : [item];
        if (picked.Count == 0 && item is not null)
            picked = [item];
        return picked.SelectMany(i => PathsOf(i.Entry)).Distinct(StringComparer.Ordinal).ToList();
    }

    private static string FilesLabel(IReadOnlyList<string> paths)
        => paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} ファイル";

    /// <summary>作業ツリーの変更をパッチとしてクリップボードへ（§24.79）。HEAD との差で、未追跡のファイルも含む。
    /// 別の PC・リポジトリ・ノート・チャットへ「この変更だけ」を運ぶ素材——当てる側は §24.38.1 の「クリップボードのパッチを当てる」。</summary>
    [RelayCommand]
    private async Task CopyChangesPatchAsync(GitChangeItem? item)
    {
        var paths = PathsForChangesAction(item);
        if (paths.Count == 0) return;
        var (text, result) = await _git.GetWorkingPatchTextAsync(paths);
        if (text is null)
        {
            StatusMessage = $"パッチを作れませんでした: {Truncate(result.Message)}";
            StatusIsError = true;
            return;
        }
        if (text.Length == 0)
        {
            StatusMessage = $"{FilesLabel(paths)} に HEAD との差がありません。";
            return;
        }
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            StatusMessage = "クリップボードが使えませんでした。もう一度試してください。";
            StatusIsError = true;
            return;
        }
        StatusMessage = $"{FilesLabel(paths)} の変更をパッチとしてコピーしました。";
        StatusIsError = false;
    }

    /// <summary>作業ツリーの変更をパッチとして保存し、エディタで開く（§24.79。スタッシュの保存と同じ形）。</summary>
    [RelayCommand]
    private async Task SaveChangesPatchAsync(GitChangeItem? item)
    {
        var paths = PathsForChangesAction(item);
        if (paths.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "変更をパッチとして保存",
            Filter = "パッチ (*.patch)|*.patch|すべてのファイル (*.*)|*.*",
            FileName = paths.Count == 1 ? Path.GetFileNameWithoutExtension(paths[0]) + ".patch" : "changes.patch",
        };
        if (_git.RootPath is { Length: > 0 } root && Directory.Exists(root))
            dialog.InitialDirectory = root;
        var owner = Application.Current?.MainWindow;
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return;

        var path = dialog.FileName;
        var result = await _git.WriteWorkingPatchAsync(paths, path);
        if (!result.Success)
        {
            StatusMessage = $"パッチの保存に失敗しました: {Truncate(result.Message)}";
            StatusIsError = true;
            return;
        }
        StatusMessage = $"{FilesLabel(paths)} の変更を {Path.GetFileName(path)} に保存しました。";
        StatusIsError = false;
        await _editor.OpenFileAsync(path);
    }

    /// <summary>クリップボードのパッチを作業ツリーへ当てる（§24.38.1）。</summary>
    [RelayCommand]
    private async Task ApplyPatchFromClipboardAsync()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { text = ""; }
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "クリップボードにパッチ（差分の文字）がありません。";
            StatusIsError = true;
            return;
        }
        await ApplyPatchAsync("クリップボードのパッチ", () => _git.PreviewPatchTextAsync(text));
    }

    /// <summary>ファイルを選んでパッチを当てる。</summary>
    [RelayCommand]
    private async Task ApplyPatchFromFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "当てるパッチを選択",
            Filter = "パッチ (*.patch;*.diff)|*.patch;*.diff|すべてのファイル (*.*)|*.*",
        };
        if (_git.RootPath is { Length: > 0 } root && Directory.Exists(root))
            dialog.InitialDirectory = root;
        var owner = Application.Current?.MainWindow;
        if ((owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return;
        await ApplyPatchFileAsync(dialog.FileName);
    }

    /// <summary>このパッチのファイルを当てる（エディタで開いた <c>.patch</c> の右クリックからも）。</summary>
    public Task ApplyPatchFileAsync(string patchFile)
        => ApplyPatchAsync(Path.GetFileName(patchFile), () => _git.PreviewPatchFileAsync(patchFile));

    /// <summary>当てる前に、どのファイルにどれだけ当たるか・どの方式で当たるかを見せて確かめる。
    /// 確認の文言は <see cref="DescribePatch"/>（純粋・テスト対象）。</summary>
    private async Task ApplyPatchAsync(string source, Func<Task<GitPatchPreview>> preview)
    {
        if (IsBusy) return;
        var checkedPatch = await preview();
        try
        {
            await ApplyCheckedPatchAsync(source, checkedPatch);
        }
        finally
        {
            checkedPatch.Discard();
        }
    }

    private async Task ApplyCheckedPatchAsync(string source, GitPatchPreview checkedPatch)
    {
        if (checkedPatch.Mode == GitPatchApplyMode.None)
        {
            StatusMessage = $"{source}は当たりません: {Truncate(checkedPatch.Error ?? "")}";
            StatusIsError = true;
            return;
        }
        if (ConfirmPatch is { } confirm && !confirm("パッチを当てる", DescribePatch(source, checkedPatch)))
            return;
        GitCommandResult? applied = null;
        await RunOpAsync("パッチを当てる", async () => applied = await _git.ApplyPatchAsync(checkedPatch));
        if (applied is null) return;
        if (applied.Success)
            StatusMessage = checkedPatch.Mode == GitPatchApplyMode.ThreeWay
                ? $"{source}を 3-way で当てました（{checkedPatch.Files.Count} ファイル・当たった変更はステージ済み）。"
                : $"{source}を当てました（{checkedPatch.Files.Count} ファイル）。";
        else if (checkedPatch.Mode == GitPatchApplyMode.ThreeWay && IsConflictReport(applied.Message))
            // 3-way は衝突しても当たる（衝突したファイルに印を付けて終わる）。失敗ではなく「直す番」と伝える。
            StatusMessage = $"{source}を当てましたが、衝突したファイルがあります（印の付いたところを直してください）: {Truncate(applied.Message)}";
    }

    internal static bool IsConflictReport(string message)
        => message.Contains("with conflicts", StringComparison.OrdinalIgnoreCase)
           || message.Contains("競合", StringComparison.Ordinal);

    /// <summary>当てる前の確認（タイトル, 本文）→ 当てるなら true。ShellWindow がダイアログをつなぐ。null なら確かめずに当てる。</summary>
    public Func<string, string, bool>? ConfirmPatch { get; set; }

    internal static string DescribePatch(string source, GitPatchPreview preview)
    {
        const int shown = 12;
        var lines = preview.Files
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Take(shown)
            .Select(f => $"  {f.Key}  {f.Value.DisplayText}")
            .ToList();
        if (preview.Files.Count > shown)
            lines.Add($"  ほか {preview.Files.Count - shown} ファイル");
        var mode = preview.Mode == GitPatchApplyMode.ThreeWay
            ? "\nそのままでは当たらないので 3-way で当てます。当たった変更はステージされ、衝突したところには印が付きます。"
            : "";
        return $"{source}を作業ツリーへ当てます（{preview.Files.Count} ファイル）。\n\n{string.Join("\n", lines)}\n{mode}";
    }

    [RelayCommand]
    private Task InitRepositoryAsync() => RunOpAsync("git init", () => _git.InitAsync());

    /// <summary>
    /// リポジトリをクローンして、そのフォルダーをワークスペースへ追加する（今のフォルダーは閉じない）。
    /// URL → 親フォルダー → フォルダー名の順に聞く。git 自身と同じ既定名を初期値に入れるので、
    /// 大抵はそのまま確定できる。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        var owner = Application.Current?.MainWindow;
        var url = Views.InputDialog.Prompt(owner, "リポジトリをクローン",
            "リポジトリの URL を入力してください（例: https://github.com/user/repo.git）");
        if (string.IsNullOrWhiteSpace(url)) return;

        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "クローン先の親フォルダーを選択" };
        if (picker.ShowDialog(owner) != true) return;

        var folderName = Views.InputDialog.Prompt(owner, "リポジトリをクローン",
            "作成するフォルダー名", GitCloneService.FolderNameFrom(url));
        if (string.IsNullOrWhiteSpace(folderName)) return;

        // 実行状態（多重実行の抑止・進行中表示・結果表示）は他の git 操作と同じ土俵に乗せる
        // ——ここだけ自前で IsBusy を上げ下げすると、長いクローンの最中に他のボタンが復活する。
        GitCloneResult? outcome = null;
        await RunOpAsync($"{folderName} のクローン", async () =>
        {
            outcome = await _git.CloneAsync(url, picker.FolderName, folderName);
            return new GitCommandResult(outcome.Success ? 0 : -1, "", outcome.Message);
        });

        // ワークスペースへ足す＝エクスプローラのツリーにも出る（マルチルート）。
        // 祖先／子孫関係で拒否されても git 側は成功しているので、表示はそのまま。
        if (outcome is { Success: true })
            _workspace.AddFolder(outcome.TargetPath);
    }

    /// <summary>クローンはリポジトリでない場所でも押せる（＝ここだけ IsRepository を見ない）。</summary>
    private bool CanClone() => !IsBusy;

    private bool CanFetch() => IsRepository && HasRemote && !IsBusy;
    private bool CanPull() => IsRepository && HasRemote && HasUpstream && Behind > 0 && !IsBusy;
    private bool CanPush() => IsRepository && HasRemote && HasUpstream && Ahead > 0 && !IsBusy;

    /// <summary>強制プッシュは「送るものが無い」ときも要る（リモートを巻き戻す用途）ので Ahead を見ない。</summary>
    private bool CanPushForce() => IsRepository && HasRemote && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private Task FetchAsync() => RunOpAsync("フェッチ", () => _git.FetchAsync());

    /// <summary>いまのブランチの上流（<c>origin/main</c> 等）。プルの前の確認で使う。</summary>
    private string? _upstream;

    /// <summary>プルの前の確認（§24.64）。上流と分かれているときにプルを押すと出る。null なら出していない。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPullPreview))]
    private GitPullPreview? _pullPreview;

    public bool HasPullPreview => PullPreview is not null;

    /// <summary>
    /// プルボタン。上流と分かれている（受け取るものも、こちらだけのものもある）ときは、すぐには取り込まず、
    /// 何が来るか・ぶつかるかを見せてマージかリベースかを選ばせる（§24.64）。分かれていなければ今まで通りそのまま取り込む。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPull))]
    private async Task PullAsync()
    {
        if (Ahead > 0 && Behind > 0 && _upstream is { Length: > 0 } upstream)
        {
            var (incoming, ahead) = (Behind, Ahead);
            var commits = await _git.GetIncomingCommitsAsync(upstream);
            var forecast = await _git.ForecastUpstreamConflictsAsync(upstream);
            // 待っている間に状態が変わった（フェッチ・他の操作）なら、古い数の確認は出さない。
            if (incoming == Behind && ahead == Ahead)
            {
                PullPreview = new GitPullPreview(upstream, incoming, ahead, commits,
                    forecast.Error is null ? forecast.Files : Array.Empty<string>(), forecast.Error);
                return;
            }
        }
        await PullMergeAsync();
    }

    /// <summary>確認を挟まずにマージで取り込む（右クリックの「マージで取り込む」・確認の「マージで取り込む」）。</summary>
    [RelayCommand(CanExecute = nameof(CanPull))]
    private Task PullMergeAsync()
    {
        PullPreview = null;
        return RunOpAsync("プル", () => _git.PullAsync());
    }

    [RelayCommand]
    private void CancelPullPreview() => PullPreview = null;

    /// <summary>取り込み方を選んでプルする（プルボタンの右クリックメニュー）。</summary>
    [RelayCommand(CanExecute = nameof(CanPull))]
    private Task PullRebaseAsync()
    {
        PullPreview = null;
        return RunOpAsync("プル（リベース）", () => _git.PullAsync(GitPullMode.Rebase));
    }

    [RelayCommand(CanExecute = nameof(CanPull))]
    private Task PullFastForwardAsync()
    {
        PullPreview = null;
        return RunOpAsync("プル（早送りのみ）", () => _git.PullAsync(GitPullMode.FastForwardOnly));
    }

    [RelayCommand(CanExecute = nameof(CanPush))]
    private Task PushAsync() => RunOpAsync("プッシュ", () => _git.PushAsync());

    /// <summary>強制プッシュ（<c>--force-with-lease</c>）。リベース・amend の後に押し直すための入口。</summary>
    [RelayCommand(CanExecute = nameof(CanPushForce))]
    private async Task PushForceAsync()
    {
        if (!GitBranchDialogs.ConfirmForcePush(
            Application.Current?.MainWindow, BranchLabel))
            return;
        await RunOpAsync("強制プッシュ", () => _git.PushAsync(force: true));
    }

    [RelayCommand]
    private Task UnstageAllAsync() => Capabilities.CanUnstage
        ? RunOpAsync("全アンステージ", () => _git.UnstageAllAsync())
        : Task.CompletedTask;

    [RelayCommand]
    private Task StageAsync(GitChangeItem? item) => item is null || !Capabilities.CanStage
        ? Task.CompletedTask
        : RunOpAsync("ステージ", () => _git.StageAsync(item.Entry.Path));

    [RelayCommand]
    private Task UnstageAsync(GitChangeItem? item) => item is null || !Capabilities.CanUnstage
        ? Task.CompletedTask
        : RunOpAsync("アンステージ", () => _git.UnstageAsync(item.Entry.Path));

    [RelayCommand]
    private async Task DiscardAsync(GitChangeItem? item)
    {
        // ビュー側でも出さないが、基準がブランチ／分岐点のときは概念として存在しない操作なので二重に塞ぐ。
        if (item is null || !Capabilities.CanDiscard) return;
        var detail = item.Entry.IsUntracked ? "未追跡ファイルなので削除されます。" : "作業ツリーの変更が失われます。";
        var answer = MessageBox.Show(
            Application.Current?.MainWindow!,
            $"{item.Entry.Path} の変更を破棄しますか？\n{detail}",
            "変更の破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        await RunOpAsync("破棄", () => _git.DiscardAsync(item.Entry));
    }

    /// <summary>ステージ済みリストで選択中のファイルをまとめてアンステージする。</summary>
    [RelayCommand]
    private Task UnstageSelectedAsync()
    {
        if (!Capabilities.CanUnstage) return Task.CompletedTask;
        var paths = _stagedSelection.Select(i => i.Entry.Path).ToArray();
        return paths.Length == 0 ? Task.CompletedTask : RunOpAsync("アンステージ", () => _git.UnstageAsync(paths));
    }

    /// <summary>
    /// コミット。ステージ済みはそのまま、ツリーでチェックした作業ツリーのファイルは
    /// コミット直前にステージしてから含める。amend は直前のコミットメッセージを維持する。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync()
    {
        // コミットはインデックスの内容を確定する操作。基準がブランチ／分岐点のときは概念として存在しない。
        if (!Capabilities.CanCommit) return;
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            StatusMessage = "コミットメッセージを入力してください。";
            StatusIsError = true;
            return;
        }
        var message = CommitMessage.Trim();
        var amend = Amend;
        var amendTarget = SelectedAmendCommit;
        var sign = Sign;
        if (amend && amendTarget is null)
        {
            StatusMessage = "amend 対象のコミットを選択してください。";
            StatusIsError = true;
            return;
        }
        // チェックした作業ツリーのファイルをステージ対象にする。リネームは新旧パスを両方含める。
        var pathsToStage = Changes.Concat(UnversionedFiles).Where(i => i.IsChecked)
            .SelectMany(i => PathsOf(i.Entry)).Distinct(StringComparer.Ordinal).ToArray();
        if (!amend && Staged.Count == 0 && pathsToStage.Length == 0)
        {
            StatusMessage = "コミットするファイルをチェックしてください。";
            StatusIsError = true;
            return;
        }
        if (WarnProblemsBeforeCommit(Staged.Select(i => i.Entry.Path).Concat(pathsToStage)) is { } warning)
        {
            StatusMessage = warning;
            StatusIsError = true;
            return;
        }
        if (amend && await WarnPublishedBeforeAmendAsync(amendTarget!.Hash) is { } published)
        {
            StatusMessage = published;
            StatusIsError = true;
            return;
        }
        await RunOpAsync(amend ? "コミット（amend）" : "コミット", async () =>
        {
            if (pathsToStage.Length > 0)
            {
                var stage = await _git.StageAsync(pathsToStage);
                if (!stage.Success) return stage;
            }
            if (!amend) return await _git.CommitAsync(message, sign: sign);

            // HEAD は通常の --amend、過去のコミットは fixup + rebase で対象へ統合する。
            return await _git.AmendCommitAsync(amendTarget!.Hash, sign);
        });
        if (!StatusIsError)
        {
            if (!amend)
            {
                CommitMessage = "";
                _commitDrafts.Put(_commitDraftRoot, "");
            }
            // amend の後は、amend を立てる前に書いていた下書きが欄へ戻る（OnAmendChanged）。
            Amend = false;
            Sign = false;
        }
    }

    /// <summary>ファイルのエラーの数を引く口（§24.34）。ShellWindow が Problems の索引（§24.33）をつなぐ。
    /// つながっていなければ確かめない。</summary>
    public Func<string, ProblemMark?>? ProblemLookup { get; set; }

    /// <summary>「それでもコミットする」と一度見せた警告の中身。同じ中身のまま押し直されたら、そのまま通す。</summary>
    private string? _acknowledgedProblemWarning;

    /// <summary>プッシュ済みと一度知らせた amend の対象（§24.78）。同じ対象のまま押し直されたら、そのまま通す。</summary>
    private string? _acknowledgedPublishedAmend;

    /// <summary>amend の対象がもうプッシュされていれば、1 回目は amend せずに知らせる（§24.78）。
    /// エラーの警告（§24.34）と同じく、ダイアログは出さず同じ対象でもう一度押せばそのまま通す。</summary>
    internal async Task<string?> WarnPublishedBeforeAmendAsync(string hash)
    {
        var remotes = await _git.GetRemoteBranchesContainingAsync(hash);
        if (GitPublishedHistory.Describe(remotes) is not { } warning)
        {
            _acknowledgedPublishedAmend = null;
            return null;
        }
        if (hash == _acknowledgedPublishedAmend)
        {
            _acknowledgedPublishedAmend = null;
            return null;
        }
        _acknowledgedPublishedAmend = hash;
        return warning + "もう一度コミットを押すと、そのまま amend します。";
    }

    /// <summary>
    /// コミットするファイルにエラーが残っていれば、1 回目はコミットせずに知らせる（§24.34・VS Code 1.98 の
    /// 「Diagnostics commit hook」）。ダイアログは出さない——同じ中身でもう一度押せばそのままコミットする。
    /// エラーが変われば（直した・増えた）また知らせる。警告は数えない（止めるほどのものではない）。
    /// </summary>
    internal string? WarnProblemsBeforeCommit(IEnumerable<string> relativePaths)
    {
        var root = _git.RootPath;
        if (ProblemLookup is null || string.IsNullOrEmpty(root)) return null;
        var withErrors = relativePaths
            .Distinct(StringComparer.Ordinal)
            .Select(path => (Path: path, Mark: ProblemLookup(Path.Combine(root, path))))
            .Where(x => x.Mark is { Errors: > 0 })
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ToList();
        if (withErrors.Count == 0)
        {
            _acknowledgedProblemWarning = null;
            return null;
        }
        var signature = string.Join("|", withErrors.Select(x => $"{x.Path}:{x.Mark!.Errors}"));
        if (signature == _acknowledgedProblemWarning)
        {
            _acknowledgedProblemWarning = null;
            return null;
        }
        _acknowledgedProblemWarning = signature;
        var first = withErrors[0];
        var others = withErrors.Count > 1 ? $" ほか {withErrors.Count - 1} ファイル" : "";
        var total = withErrors.Sum(x => x.Mark!.Errors);
        return $"コミットするファイルにエラーが {total} 件あります（{Path.GetFileName(first.Path)}{others}）。"
            + "もう一度コミットを押すと、そのままコミットします。";
    }

    /// <summary>変更行の右クリックメニュー「ファイルを開く」：そのファイルの実体をエディタで開く
    /// （差分ではなくファイルそのもの。差分はダブルクリック／「差分を開く」の <see cref="OpenDiffCommand"/>）。
    /// 削除済みエントリは開く実体が無いので何もしない。</summary>
    [RelayCommand]
    private async Task OpenFileAsync(GitChangeItem? item)
    {
        if (item is null) return;
        var root = _git.RootPath;
        if (string.IsNullOrEmpty(root)) return;
        var fullPath = Path.Combine(root, item.Entry.Path);
        if (!File.Exists(fullPath)) return;  // 削除済みなど、開く実体が無い
        await _editor.OpenFileAsync(fullPath);
    }

    /// <summary>変更行のダブルクリック／コンテキストメニュー「差分を開く」：その作業ツリー差分を見せてほしいと頼む
    /// （Diff ペインで開くか、ペインが隠れていれば別ウィンドウかは ShellWindow が決める）。</summary>
    [RelayCommand]
    private void OpenDiff(GitChangeItem? item)
    {
        if (item is null) return;
        DiffOpenRequested?.Invoke(this, new DiffOpenTarget.WorkingTreeFile(item.Entry, item.IsStaged));
    }

    /// <summary>更新系操作の共通枠：多重実行の抑止・結果メッセージの表示。</summary>
    private async Task RunOpAsync(string label, Func<Task<GitCommandResult>> operation)
    {
        if (IsBusy) return;
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
        }
        catch (Exception ex)
        {
            ReportGitException(label, ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Git操作中の予期せぬ例外もUIへ戻し、fire-and-forget経路から未処理にしない。</summary>
    private void ReportGitException(string operation, Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[Git] {operation} に失敗: {ex}");
        StatusIsError = true;
        StatusMessage = $"{operation}に失敗しました: {Truncate(ex.Message)}";
    }

    private static string Truncate(string text)
    {
        var t = text.Trim();
        return t.Length <= 300 ? t : t[..300] + "…";
    }

    /// <summary>pathspec 用のパス。リネームは旧パスも含め、旧パスの削除を同じコミットへ入れる。</summary>
    private static IEnumerable<string> PathsOf(GitChangeEntry entry)
    {
        if (entry.OrigPath is not null) yield return entry.OrigPath;
        yield return entry.Path;
    }
}
