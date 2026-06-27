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

    /// <summary>テスト用：進行中の git 状態読込＋ツリー反映が完了するまで待つ
    /// （非フィルタ時はこの完了で <see cref="Nodes"/> が投入済みになる）。</summary>
    internal Task WhenTreeLoadedAsync() => _gitLoadTask;

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

    // バックグラウンドのフィルタ構築が Nodes に反映され終わったタイミング。
    // View 側が先頭ヒットの選択・件数表示を行うために購読する。
    public event EventHandler? FilterCompleted;

    public FolderTreeViewModel(IWorkspaceService workspace, IAiWarmup warmup, WorkflowStore workflows)
    {
        _workspace = workspace;
        _warmup = warmup;
        _workflows = workflows;
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
        _workspace.OpenFolder(path);
        _workspaceRoot = Path.GetFullPath(path);

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
    private void ExpandAll()
    {
        foreach (var node in Nodes)
            ExpandRecursive(node, depth: 0);
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in Nodes)
            CollapseRecursive(node);
    }

    partial void OnHideIgnoredFilesChanged(bool value) => RefreshWorkspace();

    partial void OnShowChangedOnlyChanged(bool value) => RefreshWorkspace();

    // フィルタ文字の変更はツリーの再構築だけでよい（git 状態の再取得は不要）。
    // 文字入力を止めないよう、再構築はデバウンス＋バックグラウンドで行う（ScheduleFilter）。
    // ハイライト用の SearchFilter バインドは即時更新されるので、入力の手応えは保たれる。
    partial void OnSearchFilterChanged(string value) => ScheduleFilter(value);

    private static void ExpandRecursive(FileNodeViewModel node, int depth)
    {
        if (!node.IsDirectory || depth > FolderTreeFilter.MaxDepth || FolderTreeFilter.IsReparsePoint(node.FullPath))
            return;

        node.IsExpanded = true;

        foreach (var child in node.Children)
            ExpandRecursive(child, depth + 1);
    }

    private static void CollapseRecursive(FileNodeViewModel node)
    {
        if (!node.IsDirectory)
            return;

        node.IsExpanded = false;

        foreach (var child in node.Children)
            CollapseRecursive(child);
    }

    private bool IsFiltering => !string.IsNullOrEmpty(SearchFilter);

    private bool MatchesFilter(string name)
        => name.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase);

    private void RefreshWorkspace()
    {
        if (_currentRoot is null) return;
        // git 状態はバックグラウンドで読み、完了後に ReloadNodes でツリーへ反映する
        // （RefreshGitStateAsync の継続が ReloadNodes を呼ぶ）。
        RefreshGitStateAsync();
    }

    private void ReloadNodes()
    {
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
        ReconcileChildren(Nodes, _currentRoot);

        HasVisibleNodes = Nodes.Count > 0;
        EmptyMessage = CreateEmptyMessage();
    }

    // ファイル監視からの再描画で Nodes を丸ごと作り直すと、既存の TreeViewItem コンテナが
    // 破棄され、選択・展開状態とキーボードフォーカスが毎回失われる（監視は bin/obj/.git 等の
    // 更新で頻発するため、展開してもすぐ畳まれフォーカスも外れて見える）。そこで Clear せず、
    // FullPath をキーに既存インスタンスを再利用しながら、増減・並びの差分だけを反映する。
    // 内容が変わらなければコレクションは無変更＝コンテナもフォーカスも維持される。
    private void ReconcileChildren(ObservableCollection<FileNodeViewModel> target, string path)
    {
        var desired = EnumerateChildren(path).ToList();
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
                    ReconcileChildren(keep.Children, keep.FullPath);
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

    // フィルタの実体。入力連打中は debounce で再構築を間引き、重い列挙＋git は
    // Task.Run でバックグラウンド実行する。後続の入力が来たら CancellationToken で打ち切る。
    private async void ScheduleFilter(string query)
    {
        // 進行中のフィルタを打ち切る。古い CTS の Dispose は、その所有タスク自身が
        // finally で行う（ここで Dispose すると、まだ token を見ている処理が
        // ObjectDisposedException を投げ得るため）。
        _filterCts?.Cancel();

        if (_currentRoot is null)
            return;

        if (string.IsNullOrEmpty(query))
        {
            // 解除はトップレベルのみで軽いので即時反映。
            _filterCts = null;
            ReloadNodes();
            FilterCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        var cts = new CancellationTokenSource();
        _filterCts = cts;
        var token = cts.Token;
        var root = _currentRoot;

        try
        {
            await Task.Delay(160, token);   // 入力が続く間は再構築しない
            var built = await Task.Run(() => BuildFilteredTree(root, token), token);
            token.ThrowIfCancellationRequested();

            // ここは await 後＝UI スレッド。Nodes を一括で差し替える。
            Nodes.Clear();
            foreach (var node in built)
                Nodes.Add(node);

            HasVisibleNodes = Nodes.Count > 0;
            EmptyMessage = CreateEmptyMessage();
            FilterCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // 後続の入力／更新に置き換えられた。表示はそのまま残す。
        }
        catch
        {
            // 想定外の I/O 等。async void なのでここで握りつぶし、表示は維持する
            // （UI スレッドへ伝播するとアプリが落ちるため。git 系の例外と同じ防御方針）。
        }
        finally
        {
            // 自分が現役なら参照を外し、いずれにせよ自分の CTS は確実に破棄する。
            if (ReferenceEquals(_filterCts, cts))
                _filterCts = null;
            cts.Dispose();
        }
    }

    // フィルタの実体は FolderTreeFilter（Services）。表示条件・git 問い合わせだけを渡す。
    private List<FileNodeViewModel> BuildFilteredTree(string root, CancellationToken token)
    {
        var entries = FolderTreeFilter.BuildFilteredTree(
            root,
            MatchesFilter,
            ShouldShow,
            _gitState.GetIgnoredPaths,
            computeIgnored: HideIgnoredFiles && !ShowChangedOnly,
            token);
        return entries.Select(ToNode).ToList();
    }

    // フィルタ結果（UI 非依存の Entry）を表示用ノードへ変換する。一致フォルダは自動展開して
    // 埋もれたヒットを見せる。reparse point は展開しない葉として見せる。
    private FileNodeViewModel ToNode(FolderTreeFilter.Entry entry)
    {
        var node = new FileNodeViewModel(entry.FullPath, entry.IsDirectory, this);
        if (entry.IsReparseLeaf)
        {
            node.LoadChildren(Array.Empty<FileNodeViewModel>());
        }
        else if (entry.IsDirectory)
        {
            node.LoadChildren(entry.Children.Select(ToNode).ToList());
            node.IsExpanded = true;
        }
        return node;
    }

    private IEnumerable<FileNodeViewModel> EnumerateChildren(string path)
    {
        if (!Directory.Exists(path)) yield break;

        var directories = Directory.EnumerateDirectories(path).ToArray();
        var files = Directory.EnumerateFiles(path).ToArray();
        // 「変更のみ表示」では変更ファイル集合だけを通すため、ignore 判定は不要
        // （git が変更として報告するパスは ignore 対象になり得ない）。これにより、
        // 変更フォルダを再帰的に自動展開する際にディレクトリ階層ごとに
        // `git check-ignore` を同期起動して UI を固める問題を避ける。
        var ignoredPaths = (HideIgnoredFiles && !ShowChangedOnly)
            ? _gitState.GetIgnoredPaths(directories.Concat(files))
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var visibleDirectories = directories
            .OrderBy(d => Path.GetFileName(d))
            .Where(d => ShouldShow(d, isDirectory: true, ignoredPaths))
            .Select(d =>
            {
                var node = new FileNodeViewModel(d, true, this);
                // 「変更のみ表示」では変更ファイルがフォルダの奥に埋もれて見えなくなるため、
                // 該当フォルダを自動展開して中の追加/変更ファイルをそのまま見せる。
                // （展開すると子も同じ経路でフィルタ＆自動展開され、末端まで再帰的に開く）
                if (ShowChangedOnly) node.IsExpanded = true;
                return node;
            });

        var visibleFiles = files
            .OrderBy(f => Path.GetFileName(f))
            .Where(f => ShouldShow(f, isDirectory: false, ignoredPaths))
            .Select(f => new FileNodeViewModel(f, false, this));

        foreach (var node in visibleDirectories.Concat(visibleFiles))
            yield return node;
    }

    public IEnumerable<FileNodeViewModel> Children(string dirPath) => EnumerateChildren(dirPath);

    private bool ShouldShow(string path, bool isDirectory, HashSet<string> ignoredPaths)
    {
        var fullPath = Path.GetFullPath(path);

        if (HideIgnoredFiles && ignoredPaths.Contains(fullPath))
            return false;

        if (!ShowChangedOnly)
            return true;

        return isDirectory
            ? _gitState.ChangedDirectories.Contains(fullPath)
            : _gitState.ChangedFiles.Contains(fullPath);
    }

    // ツリー上のノードに付ける差分マークの種別。フォルダは配下に変更を含むなら DirectoryChanged。
    internal GitChangeKind GitStatusFor(string fullPath, bool isDirectory)
    {
        if (isDirectory)
            return _gitState.ChangedDirectories.Contains(Path.GetFullPath(fullPath))
                ? GitChangeKind.DirectoryChanged
                : GitChangeKind.None;
        return _gitState.GetFileStatus(fullPath);
    }

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

    /// <summary>git 状態（rev-parse / status / check-ignore のためのインデックス）をバックグラウンドで
    /// 読み込み、完了後に UI スレッドで <see cref="_gitState"/> を差し替えて <see cref="ReloadNodes"/> で
    /// ツリー（ignore 非表示・差分マーク）へ反映する。git プロセス起動は大きいリポジトリで数百ms〜と
    /// 重く、これを UI スレッドで同期実行するとワークスペース切替・監視更新が固まるため逃がす。
    /// 読込中は既存のツリー表示をそのまま残す（呼び出し側が必要なら先に Nodes をクリアする）。</summary>
    private void RefreshGitStateAsync()
    {
        _gitLoadCts?.Cancel();

        if (_currentRoot is null)
        {
            _gitLoadCts = null;
            _gitState = GitTreeState.Empty;
            FilterStatus = "";
            ReloadNodes();
            _gitLoadTask = Task.CompletedTask;
            return;
        }

        var root = _currentRoot;
        var cts = new CancellationTokenSource();
        _gitLoadCts = cts;
        _gitLoadTask = LoadGitStateAndReloadAsync(root, cts);
    }

    private async Task LoadGitStateAndReloadAsync(string root, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            var state = await Task.Run(() => GitTreeState.Load(root), token);
            token.ThrowIfCancellationRequested();

            // 切替・別ルート選択・更新で置き換えられていたら、この結果は捨てる
            // （await 後は UI スレッド。_gitState/Nodes の操作はここで安全に行える）。
            if (!ReferenceEquals(_gitLoadCts, cts)
                || _currentRoot is null
                || !PathsEqual(_currentRoot, root))
                return;

            _gitState = state;
            ApplyFilterStatus(state);
            ReloadNodes();
        }
        catch (OperationCanceledException)
        {
            // 後続のルート切替・更新に置き換えられた。表示はそのまま。
        }
        catch
        {
            // git／I-O の想定外例外は握りつぶす（async void なので UI へ伝播させない。
            // 既存の git 例外防御方針と同じ）。
        }
        finally
        {
            if (ReferenceEquals(_gitLoadCts, cts))
                _gitLoadCts = null;
            cts.Dispose();
        }
    }

    private void ApplyFilterStatus(GitTreeState state)
    {
        var filters = new List<string>();
        if (HideIgnoredFiles) filters.Add("ignore 非表示");
        if (ShowChangedOnly) filters.Add("変更のみ");

        var gitStatus = state.IsGitRepository
            ? $"{state.ChangedFiles.Count} 件変更"
            : "Git 未検出";
        FilterStatus = filters.Count == 0
            ? gitStatus
            : $"{string.Join(" / ", filters)} - {gitStatus}";
    }

    private void StartWatching(string path)
    {
        _watcher ??= new DebouncedFolderWatcher(RefreshWorkspace);
        _watcher.Watch(path);
    }
}
