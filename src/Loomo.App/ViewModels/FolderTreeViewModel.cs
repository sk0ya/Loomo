using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private readonly IAiWarmup _warmup;
    private readonly WorkflowStore _workflows;
    private readonly FolderTreeCommandHandler _fileCommands;
    private readonly FolderTreeQuery _query;
    private GitTreeState _gitState = GitTreeState.Empty;
    // ワークスペースの真のルート（ツール・ターミナルの基準。OpenFolder で確定し、表示切替では変えない）。
    private string? _workspaceRoot;
    // ツリーに表示中のルート。ピン留めフォルダへの切替で _workspaceRoot 配下のフォルダになり得る。
    private string? _currentRoot;
    // ピン留めフォルダ（正規化済みフルパス）。バックグラウンドのフィルタ構築からも参照されるため、
    // 変更時はインスタンスごと差し替える（enumeration 中の変更による例外を避ける）。
    private HashSet<string> _pinnedPaths = new(StringComparer.OrdinalIgnoreCase);
    // LoadRoot / ピン解除での選択肢再構築中に、ComboBox からの選択変更（SelectedRootOption）で
    // 表示切替・保存イベントが多重発火しないよう抑止するフラグ。
    private bool _suppressRootSelection;
    // ファイル監視（デバウンス・UIスレッドへのディスパッチ込み）。実体は DebouncedFolderWatcher。
    private DebouncedFolderWatcher? _watcher;
    // 進行中のフィルタ（デバウンス＋バックグラウンド構築）を、後続の入力で打ち切るためのトークン。
    private CancellationTokenSource? _filterCts;
    // 進行中の git 状態読込（バックグラウンドの git プロセス起動）を、後続のルート切替・更新で
    // 打ち切るためのトークン。ワークスペース切替が git の同期起動で固まらないようにする。
    private CancellationTokenSource? _gitLoadCts;
    // 直近の git 状態読込タスク（読込→ツリー反映まで）。テストが反映完了を待つための継ぎ目。
    private Task _gitLoadTask = Task.CompletedTask;

    // マルチルート（複数ワークスペースフォルダー）時のフォルダーごとの表示状態。空＝単一フォルダー
    // （既存の _workspaceRoot/_currentRoot/_pinnedPaths/RootOptions/_gitState/_watcher 経路をそのまま使う）。
    private Dictionary<string, FolderTreeRootState> _multiRootStates = new(StringComparer.OrdinalIgnoreCase);

    // LoadRoot 内の _workspace.OpenFolder 呼び出しで発火する FoldersChanged を、LoadRoot 自身の
    // 初期化ロジックと二重処理しないよう抑止するフラグ（OpenFolder は LoadRoot からしか呼ばれない）。
    private bool _suppressFoldersChangedReaction;

    /// <summary>テスト用：進行中の git 状態読込＋ツリー反映が完了するまで待つ
    /// （非フィルタ時はこの完了で <see cref="Nodes"/> が投入済みになる）。</summary>
    internal Task WhenTreeLoadedAsync() => _gitLoadTask;

    /// <summary>複数フォルダーワークスペースか（true のとき <see cref="Nodes"/> はフォルダーごとの
    /// 見出しノードの並びになり、ルート切替 ComboBox は隠れる——ピン切替は見出しの右クリックに移る）。</summary>
    public bool IsMultiRootWorkspace => _multiRootStates.Count > 0;

    [ObservableProperty]
    private string _rootLabel = "(フォルダ未選択)";

    [ObservableProperty]
    private bool _hideIgnoredFiles = true;

    [ObservableProperty]
    private bool _showChangedOnly;

    [ObservableProperty]
    private string _filterStatus = "";

    // インクリメンタル検索（/）のクエリ。空でなければツリーを名前一致でフィルタする。
    // ヒットしたファイルへ至るフォルダだけを残し、自動展開して中身を見せる。
    [ObservableProperty]
    private string _searchFilter = "";

    [ObservableProperty]
    private bool _hasVisibleNodes;

    [ObservableProperty]
    private string _emptyMessage = "";

    public ObservableCollection<FileNodeViewModel> Nodes { get; } = new();

    // ルート切替 ComboBox の候補。先頭がワークスペースルート、以降がピン留めフォルダ。
    public ObservableCollection<FolderRootOption> RootOptions { get; } = new();

    [ObservableProperty]
    private FolderRootOption? _selectedRootOption;

    // ピン留めの追加/解除・表示ルートの切替があったとき。ShellWindow が購読して
    // ワークスペーススナップショットへ保存する。
    public event EventHandler? RootStateChanged;

    public event EventHandler<string>? FileActivated;

    // ファイル／フォルダのリネーム後（旧フルパス → 新フルパス）。ShellWindow が購読し、開いている
    // エディタタブのパス・タブ名を新パスへ追従させる。フォルダのリネームでは配下のファイルも対象。
    public event EventHandler<EntryRenamedEventArgs>? EntryRenamed;

    // ファイル／フォルダの削除後（ゴミ箱送り）。ShellWindow が購読し、該当（フォルダなら配下）の
    // エディタタブを閉じる。
    public event EventHandler<string>? EntryDeleted;

    // ファイルの単クリックでのプレビュー表示要求。ShellWindow がプレビュータブ
    // （編集するまで確定せず、次のクリックで中身が差し替わる）で開く。
    public event EventHandler<string>? FilePreviewRequested;

    // FolderTree の HTML をアプリ内ブラウザで開く要求。View（コンテキストメニュー）から発火し、
    // ShellWindow がブラウザペインに新規タブを開いて file:// URL をナビゲートする。
    public event EventHandler<string>? OpenInBrowserRequested;

    // FolderTree の項目を可視ターミナルへ「セット」する要求。View（コンテキストメニュー）から発火し、
    // ShellWindow が処理する：フォルダはそのフォルダへ cd、ファイルはパスをプロンプトへ入力（未実行）。
    public event EventHandler<TerminalSetRequest>? SetInTerminalRequested;

    // FolderTree の「このフォルダーで検索」要求（フォルダのみ）。View（コンテキストメニュー）から発火し、
    // ShellWindow が検索パネルを開いて、そのフォルダを検索の開始フォルダーに設定する。
    public event EventHandler<string>? SearchInFolderRequested;

    // 表示ルートが変わったとき（フォルダを開いた・ピンルートへ切替えた）。ShellWindow が購読して
    // 検索パネルの既定の開始フォルダーへ反映する。
    public event EventHandler<string>? CurrentRootChanged;

    /// <summary>ツリーに現在表示している（ピン留めで切替わり得る）ルート。検索の既定フォルダーに使う。</summary>
    public string? CurrentRoot => _currentRoot;

    // FolderTree の「AI-誤字脱字チェック」要求。View（コンテキストメニュー）から発火し、ShellWindow が
    // AIバーで /clear → 当該ファイルパスを渡して誤字脱字チェックのプロンプトを送信する。
    public event EventHandler<string>? TypoCheckRequested;

    // FolderTree の「AIワークフロー」要求。View（コンテキストメニュー）から発火し、ShellWindow が
    // AIバーをワークフローモードへ切替えて、当該ファイルを構造化 input として実行する。
    public event EventHandler<WorkflowRunRequest>? WorkflowRequested;

    // FolderTree の「Git」>「Git Blame」要求（ファイルのみ）。View（コンテキストメニュー）から発火し、
    // ShellWindow がエディタペインでファイルを開いて VimEditorControl のネイティブ Git Blame を
    // トリガーする。引数はファイルのフルパス。
    public event EventHandler<string>? GitBlameRequested;

    // FolderTree の「Git」>「履歴を表示」要求（ファイル・フォルダ両方）。View（コンテキストメニュー）から
    // 発火し、ShellWindow が Git ペインを前面に出して、そのパスの履歴（git log -- path）に絞る。
    // 引数は対象のフルパス。
    public event EventHandler<string>? GitHistoryRequested;

    // バックグラウンドのフィルタ構築が Nodes に反映され終わったタイミング。
    // View 側が先頭ヒットの選択・件数表示を行うために購読する。
    public event EventHandler? FilterCompleted;

    // FolderTree の「現在のファイルを選択」ボタン／ショートカット。ShellWindow が購読し、
    // エディタでアクティブなファイルをツリーで展開・選択する（同期）。
    public event EventHandler? RevealCurrentFileRequested;

    public FolderTreeViewModel(IWorkspaceService workspace, IAiWarmup warmup, WorkflowStore workflows,
        FolderTreeCommandHandler fileCommands, FolderTreeQuery query)
    {
        _workspace = workspace;
        _warmup = warmup;
        _workflows = workflows;
        _fileCommands = fileCommands;
        _query = query;
        _workspace.FoldersChanged += OnWorkspaceFoldersChanged;
    }

    // AddFolder/RemoveFolder（および RemoveFolder で単一フォルダーへ戻った場合）に反応する。
    // LoadRoot 内の OpenFolder 起因の発火は _suppressFoldersChangedReaction で無視する
    // （LoadRoot 自身が初期化を完結させるため、ここで二重に走らせない）。
    private void OnWorkspaceFoldersChanged(object? sender, EventArgs e)
    {
        if (_suppressFoldersChangedReaction || _workspaceRoot is null)
            return;
        ReconcileRootStates();
        // RestoreAdditionalFolders は AddFolder 呼び出し中 _suppressFoldersChangedReaction を立てて
        // ここを素通りさせる（復元中の保存イベント多重発火を避ける）。ここに来るのは実行時の
        // 追加・削除（ボタン／「ワークスペースから削除」）だけなので、常に保存イベントを出してよい。
        RootStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>AIの暖機が完了してモデルが使える状態か。「AI-誤字脱字チェック」メニューの出し分けに使う
    /// （暖機中・モデル未ロード時はメニューを出さない）。</summary>
    public bool IsAiReady => _warmup.IsReady;

    /// <summary>
    /// ワークスペースルートを確定（ツールのパス制限・ターミナルの基準はこのルート）し、
    /// ピン留め候補を復元してツリーを表示する。treeRootPath が候補にあればそれを表示ルートにする。
    /// </summary>
    public void LoadRoot(string path, IReadOnlyList<string>? pinnedFolders = null, string? treeRootPath = null)
    {
        _suppressFoldersChangedReaction = true;
        try { _workspace.OpenFolder(path); }
        finally { _suppressFoldersChangedReaction = false; }
        _workspaceRoot = Path.GetFullPath(path);

        foreach (var state in _multiRootStates.Values)
            state.Watcher?.Dispose();
        _multiRootStates.Clear();

        BuildRootOptions(pinnedFolders ?? Array.Empty<string>());

        var initial = treeRootPath is null ? null : FindRootOption(treeRootPath);
        SelectRootOption(initial ?? RootOptions[0]);
    }

    /// <summary>スナップショット保存用のピン一覧（フルパス）。</summary>
    public IReadOnlyList<string> PinnedFolders
        => RootOptions.Where(o => o.IsPinned).Select(o => o.FullPath).ToList();

    /// <summary>表示中ルートがワークスペースルートと異なるときだけそのパス（保存用）。</summary>
    public string? TreeRootOverride
        => _workspaceRoot is null || _currentRoot is null || PathsEqual(_currentRoot, _workspaceRoot)
            ? null
            : _currentRoot;

    [RelayCommand]
    private void Refresh()
    {
        if (_currentRoot is null) return;
        RefreshWorkspace();
    }

    [RelayCommand]
    private void RevealCurrentFile() => RevealCurrentFileRequested?.Invoke(this, EventArgs.Empty);

    partial void OnHideIgnoredFilesChanged(bool value) => RefreshWorkspace();

    partial void OnShowChangedOnlyChanged(bool value) => RefreshWorkspace();

    // フィルタ文字の変更はツリーの再構築だけでよい（git 状態の再取得は不要）。
    // 文字入力を止めないよう、再構築はデバウンス＋バックグラウンドで行う（ScheduleFilter）。
    // ハイライト用の SearchFilter バインドは即時更新されるので、入力の手応えは保たれる。
    partial void OnSearchFilterChanged(string value) => ScheduleFilter(value);

    private bool IsFiltering => !string.IsNullOrEmpty(SearchFilter);

    private bool MatchesFilter(string name)
        => name.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase);

    private void RefreshWorkspace()
    {
        if (_multiRootStates.Count == 0 && _currentRoot is null) return;
        // git 状態はバックグラウンドで読み、完了後に ReloadNodes でツリーへ反映する
        // （RefreshGitStateAsync の継続が ReloadNodes を呼ぶ）。
        RefreshGitStateAsync();
    }

    private void ReloadNodes()
    {
        // マルチルート時は各フォルダーの見出しの Children を Git.cs 側（状態ごとの読込完了）で
        // 個別に反映するため、ここでは何もしない（Nodes 自体の増減は RebuildMultiRootNodes の担当）。
        if (_multiRootStates.Count > 0)
            return;

        if (_currentRoot is null)
        {
            _filterCts?.Cancel();
            Nodes.Clear();
            HasVisibleNodes = false;
            EmptyMessage = "フォルダを開いてください";
            return;
        }

        // フィルタ中の再構築は重い（全階層の列挙＋git）ため、バックグラウンドへ回す。
        // 既存表示は ScheduleFilter が完了するまで残し、ちらつきを防ぐ。
        if (IsFiltering)
        {
            ScheduleFilter(SearchFilter);
            return;
        }

        // 非フィルタ時はトップレベルのみ（遅延読込）なので同期で十分軽い。
        _filterCts?.Cancel();
        ReconcileChildren(Nodes, _currentRoot, _currentRoot);

        HasVisibleNodes = Nodes.Count > 0;
        EmptyMessage = CreateEmptyMessage();
    }

    // ファイル監視からの再描画で Nodes を丸ごと作り直すと、既存の TreeViewItem コンテナが
    // 破棄され、選択・展開状態とキーボードフォーカスが毎回失われる（監視は bin/obj/.git 等の
    // 更新で頻発するため、展開してもすぐ畳まれフォーカスも外れて見える）。そこで Clear せず、
    // FullPath をキーに既存インスタンスを再利用しながら、増減・並びの差分だけを反映する。
    // 内容が変わらなければコレクションは無変更＝コンテナもフォーカスも維持される。
    // マルチルートの見出しリスト（Nodes = フォルダーごとの見出し）も同じ diff/reuse ロジックで
    // 反映できるよう、実体は「desired（比較したい内容）を直接受け取る」 ReconcileChildrenCore
    // へ切り出してある。単一フォルダー時の呼び出し元はこのラッパーで挙動が変わらない。
    private void ReconcileChildren(ObservableCollection<FileNodeViewModel> target, string path, string rootKey)
        => ReconcileChildrenCore(target, EnumerateChildren(path, rootKey).ToList());

    private void ReconcileChildrenCore(ObservableCollection<FileNodeViewModel> target, List<FileNodeViewModel> desired)
    {
        var desiredPaths = new HashSet<string>(
            desired.Select(d => d.FullPath), StringComparer.OrdinalIgnoreCase);

        // 1. 消えた項目を除去する。
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desiredPaths.Contains(target[i].FullPath))
                target.RemoveAt(i);

        // 2. desired の順に並べ替えつつ、既存インスタンスは再利用（新規だけ挿入）。
        for (var i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            var existingIndex = IndexOfPath(target, want.FullPath);
            if (existingIndex < 0)
            {
                target.Insert(i, want);
                continue;
            }

            var keep = target[existingIndex];
            if (existingIndex != i)
                target.Move(existingIndex, i);

            // 再利用するインスタンスは git 状態が古い可能性があるのでマークを更新する。
            keep.RefreshGitStatus();

            // 展開済みディレクトリは中身も差分更新する。畳まれている枝は表示されていないので
            // 走査せず、遅延読込状態へ戻して次に開いたとき最新を読み直させる。
            if (keep.IsDirectory)
            {
                if (keep.IsExpanded)
                    ReconcileChildren(keep.Children, keep.FullPath, keep.RootKey);
                else
                    keep.ResetToLazy();
            }
        }
    }

    private static int IndexOfPath(ObservableCollection<FileNodeViewModel> nodes, string fullPath)
    {
        for (var i = 0; i < nodes.Count; i++)
            if (string.Equals(nodes[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private IEnumerable<FileNodeViewModel> EnumerateChildren(string path, string rootKey)
    {
        var state = ResolveGitState(rootKey);
        var entries = _query.EnumerateChildren(path);
        var directories = entries.Directories;
        var files = entries.Files;
        // 「変更のみ表示」では変更ファイル集合だけを通すため、ignore 判定は不要
        // （git が変更として報告するパスは ignore 対象になり得ない）。これにより、
        // 変更フォルダを再帰的に自動展開する際にディレクトリ階層ごとに
        // `git check-ignore` を同期起動して UI を固める問題を避ける。
        var ignoredPaths = (HideIgnoredFiles && !ShowChangedOnly)
            ? state.GetIgnoredPaths(directories.Concat(files))
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var visibleDirectories = directories
            .OrderBy(d => Path.GetFileName(d))
            .Where(d => ShouldShow(d, isDirectory: true, ignoredPaths, state))
            .Select(d =>
            {
                var node = new FileNodeViewModel(d, true, this, rootKey);
                // 「変更のみ表示」では変更ファイルがフォルダの奥に埋もれて見えなくなるため、
                // 該当フォルダを自動展開して中の追加/変更ファイルをそのまま見せる。
                // （展開すると子も同じ経路でフィルタ＆自動展開され、末端まで再帰的に開く）
                if (ShowChangedOnly) node.IsExpanded = true;
                return node;
            });

        var visibleFiles = files
            .OrderBy(f => Path.GetFileName(f))
            .Where(f => ShouldShow(f, isDirectory: false, ignoredPaths, state))
            .Select(f => new FileNodeViewModel(f, false, this, rootKey));

        foreach (var node in visibleDirectories.Concat(visibleFiles))
            yield return node;
    }

    public IEnumerable<FileNodeViewModel> Children(string dirPath, string rootKey) => EnumerateChildren(dirPath, rootKey);

    private bool ShouldShow(string path, bool isDirectory, HashSet<string> ignoredPaths, GitTreeState state)
    {
        var fullPath = Path.GetFullPath(path);

        if (HideIgnoredFiles && ignoredPaths.Contains(fullPath))
            return false;

        if (!ShowChangedOnly)
            return true;

        return isDirectory
            ? state.ChangedDirectories.Contains(fullPath)
            : state.ChangedFiles.Contains(fullPath);
    }

    /// <summary>rootKey に対応する Git 状態を返す。単一フォルダー時は常に <see cref="_gitState"/>
    /// （rootKey は無視——一意なのでキー引きの必要がない）。複数フォルダー時はそのフォルダーの状態、
    /// 未読込／該当なしは空状態。</summary>
    private GitTreeState ResolveGitState(string rootKey)
    {
        if (_multiRootStates.Count == 0)
            return _gitState;
        return _multiRootStates.TryGetValue(rootKey, out var state) ? state.GitState : GitTreeState.Empty;
    }

    // ツリー上のノードに付ける差分マークの種別。フォルダは配下に変更を含むなら DirectoryChanged。
    internal GitChangeKind GitStatusFor(string fullPath, bool isDirectory, string rootKey)
    {
        var state = ResolveGitState(rootKey);
        if (isDirectory)
            return state.ChangedDirectories.Contains(Path.GetFullPath(fullPath))
                ? GitChangeKind.DirectoryChanged
                : GitChangeKind.None;
        return state.GetFileStatus(fullPath);
    }

    /// <summary>rootKey が属するフォルダーが Git リポジトリ配下か（FileNodeViewModel の「Git」メニュー
    /// 出し分けに使う）。</summary>
    internal bool IsGitRepositoryFor(string rootKey) => ResolveGitState(rootKey).IsGitRepository;

    /// <summary>ワークスペースのどこかが Git リポジトリ配下か。ノードに紐付かない汎用のゲート
    /// （エディタの右クリックメニューの Git 系項目出し分け等）に使う。</summary>
    internal bool IsGitRepository => _multiRootStates.Count == 0
        ? _gitState.IsGitRepository
        : _multiRootStates.Values.Any(s => s.GitState.IsGitRepository);

    private string CreateEmptyMessage()
    {
        if (_currentRoot is null)
            return "フォルダを開いてください";

        if (IsFiltering)
            return $"「{SearchFilter}」に一致する項目はありません";

        if (ShowChangedOnly && !_gitState.IsGitRepository)
            return "Git リポジトリではありません";

        if (ShowChangedOnly)
            return "変更されたファイルはありません";

        return "表示する項目はありません";
    }

    private void StartWatching(string path)
    {
        _watcher ??= new DebouncedFolderWatcher(RefreshWorkspace);
        _watcher.Watch(path);
    }

    // ===== マルチルート（複数ワークスペースフォルダー） =====

    /// <summary>workspace.Folders の変化（追加・削除）に、既存フォルダーの状態（Git・監視・ピン）を
    /// 極力保ったまま追従する。Folders.Count が 1 以下へ戻ったときは単一フォルダー経路へ復帰する。</summary>
    private void ReconcileRootStates()
    {
        var folders = _workspace.Folders;

        if (folders.Count <= 1)
        {
            foreach (var state in _multiRootStates.Values)
                state.Watcher?.Dispose();
            _multiRootStates.Clear();
            OnPropertyChanged(nameof(IsMultiRootWorkspace));

            if (folders.Count == 1)
                _workspaceRoot = folders[0];
            // _currentRoot を明示的に null化し、SelectRootOption に必ず SetDisplayRoot
            // （watcher・git・Nodes の再構築）を実行させる（単一フォルダー化直後は両方とも
            // 古いマルチルート表示のままなので、素通りされると復旧できない）。
            _currentRoot = null;
            BuildRootOptions(Array.Empty<string>());
            SelectRootOption(RootOptions[0]);
            return;
        }

        var next = new Dictionary<string, FolderTreeRootState>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (_multiRootStates.TryGetValue(folder, out var existing))
            {
                next[folder] = existing;
                continue;
            }

            var state = new FolderTreeRootState(folder);
            var rootOption = new FolderRootOption(folder, LabelForWithin(folder, folder), isPinned: false);
            state.RootOptions.Add(rootOption);
            state.SelectedRootOption = rootOption;
            next[folder] = state;
        }

        foreach (var kvp in _multiRootStates)
            if (!next.ContainsKey(kvp.Key))
                kvp.Value.Watcher?.Dispose();

        // 単一フォルダー時に使っていた監視・進行中読込は、複数化と同時に片付ける
        // （_currentRoot/_pinnedPaths/RootOptions 等は folders.Count<=1 へ戻ったときに作り直すので、
        // ここでは触れない）。
        _watcher?.Dispose();
        _watcher = null;
        _gitLoadCts?.Cancel();
        _gitLoadCts = null;

        _multiRootStates = next;
        OnPropertyChanged(nameof(IsMultiRootWorkspace));
        RebuildMultiRootNodes();
    }

    /// <summary>Nodes をフォルダーごとの見出しノードの並びへ再構成する（diff/reuse なので既存フォルダー
    /// の展開・フォーカス状態は保たれる）。その後、フォルダーごとに Git 状態の読込・監視を開始する。</summary>
    private void RebuildMultiRootNodes()
    {
        _filterCts?.Cancel();

        var desired = new List<FileNodeViewModel>();
        foreach (var folder in _workspace.Folders)
        {
            var state = _multiRootStates[folder];
            desired.Add(new FileNodeViewModel(state.DisplayedPath, true, this, state.FolderPath,
                isWorkspaceFolderRoot: true));
            if (state.Watcher is null)
                StartWatchingRootState(state);
        }

        ReconcileChildrenCore(Nodes, desired);

        // diff/reuse で Nodes に入った実インスタンス（新規 or 再利用）を state.HeaderNode として記録する。
        foreach (var node in Nodes)
            if (_multiRootStates.TryGetValue(node.RootKey, out var state))
                state.HeaderNode = node;

        HasVisibleNodes = Nodes.Count > 0;
        EmptyMessage = Nodes.Count == 0 ? "フォルダを開いてください" : "";

        RefreshGitStateAsync();
    }

    private void StartWatchingRootState(FolderTreeRootState state)
    {
        state.Watcher = new DebouncedFolderWatcher(() => RefreshRootState(state));
        state.Watcher.Watch(state.DisplayedPath);
    }
}
