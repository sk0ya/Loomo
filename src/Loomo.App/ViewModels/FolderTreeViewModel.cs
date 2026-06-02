using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using sk0ya.Loomo.Core.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

public sealed partial class FolderTreeViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspace;
    private GitTreeState _gitState = GitTreeState.Empty;
    private string? _currentRoot;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _refreshTimer;
    private int _refreshQueued;
    // 進行中のフィルタ（デバウンス＋バックグラウンド構築）を、後続の入力で打ち切るためのトークン。
    private CancellationTokenSource? _filterCts;

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

    public event EventHandler<string>? FileActivated;

    // バックグラウンドのフィルタ構築が Nodes に反映され終わったタイミング。
    // View 側が先頭ヒットの選択・件数表示を行うために購読する。
    public event EventHandler? FilterCompleted;

    public FolderTreeViewModel(IWorkspaceService workspace)
    {
        _workspace = workspace;
    }

    public void LoadRoot(string path)
    {
        _workspace.OpenFolder(path);
        _currentRoot = Path.GetFullPath(path);
        RootLabel = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(RootLabel)) RootLabel = path;

        RefreshGitState();
        ReloadNodes();
        StartWatching(path);
    }

    [RelayCommand]
    private void Refresh()
    {
        if (_currentRoot is null) return;
        RefreshWorkspace();
    }

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
        if (_currentRoot is null) return;
        RefreshGitState();
        ReloadNodes();
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

    // シンボリックリンク/ジャンクションの循環で無限再帰しないための保険。実在のソースツリーは
    // この深さに達しないので、超えたら打ち切る。
    private const int MaxFilterDepth = 64;

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

    private List<FileNodeViewModel> BuildFilteredTree(string root, CancellationToken token)
    {
        // ignore 判定は階層単位でまとめて git に問い合わせる（フォルダ単位の多重起動を避ける）。
        var ignoredPaths = ComputeIgnoredPaths(root, token);
        return BuildFilteredChildren(root, depth: 0, ignoredPaths, token);
    }

    // ツリーを階層ごと（BFS）に走査し、git check-ignore を「階層につき 1 回」だけ呼んで
    // ignore 集合をまとめて得る。ignore されたフォルダ・reparse point はその場で打ち切るため、
    // node_modules などの巨大ツリーへ潜らない。git 呼び出し回数は概ね「ツリーの深さ」に収まる。
    private HashSet<string> ComputeIgnoredPaths(string root, CancellationToken token)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!(HideIgnoredFiles && !ShowChangedOnly))
            return ignored;

        var frontier = new List<string> { root };
        for (var depth = 0; depth <= MaxFilterDepth && frontier.Count > 0; depth++)
        {
            token.ThrowIfCancellationRequested();

            var levelDirs = new List<string>();
            var levelEntries = new List<string>();
            foreach (var dir in frontier)
            {
                if (TryEnumerate(dir, out var subdirs, out var files))
                {
                    levelDirs.AddRange(subdirs);
                    levelEntries.AddRange(subdirs);
                    levelEntries.AddRange(files);
                }
            }

            if (levelEntries.Count == 0)
                break;

            foreach (var path in _gitState.GetIgnoredPaths(levelEntries))
                ignored.Add(path);

            // 次の階層は「ignore されておらず、reparse でない」サブフォルダのみ。
            frontier = levelDirs
                .Where(d => !ignored.Contains(Path.GetFullPath(d)) && !IsReparsePoint(d))
                .ToList();
        }

        return ignored;
    }

    // 名前が一致するノード（とそこへ至るフォルダ）だけを残し、一致フォルダは自動展開して
    // 埋もれたヒットを見せる。ignore 集合は事前計算済みなので、ここでは git を呼ばない。
    private List<FileNodeViewModel> BuildFilteredChildren(
        string path, int depth, HashSet<string> ignoredPaths, CancellationToken token)
    {
        var result = new List<FileNodeViewModel>();
        if (depth > MaxFilterDepth || !TryEnumerate(path, out var directories, out var files))
            return result;

        token.ThrowIfCancellationRequested();

        foreach (var directory in directories.OrderBy(d => Path.GetFileName(d)))
        {
            token.ThrowIfCancellationRequested();

            if (!ShouldShow(directory, isDirectory: true, ignoredPaths))
                continue;

            // reparse point（シンボリックリンク/ジャンクション）は循環の恐れがあるので辿らない。
            // 名前が一致する場合だけ、展開しない葉として見せる。
            if (IsReparsePoint(directory))
            {
                if (MatchesFilter(Path.GetFileName(directory)))
                {
                    var leaf = new FileNodeViewModel(directory, true, this);
                    leaf.LoadChildren(Array.Empty<FileNodeViewModel>());
                    result.Add(leaf);
                }
                continue;
            }

            var matchingChildren = BuildFilteredChildren(directory, depth + 1, ignoredPaths, token);
            if (!MatchesFilter(Path.GetFileName(directory)) && matchingChildren.Count == 0)
                continue;

            var node = new FileNodeViewModel(directory, true, this);
            node.LoadChildren(matchingChildren);
            node.IsExpanded = true;
            result.Add(node);
        }

        foreach (var file in files.OrderBy(f => Path.GetFileName(f)))
        {
            if (!ShouldShow(file, isDirectory: false, ignoredPaths))
                continue;
            if (MatchesFilter(Path.GetFileName(file)))
                result.Add(new FileNodeViewModel(file, false, this));
        }

        return result;
    }

    // 全階層を再帰的に列挙するため、アクセス不可なフォルダがあってもそこだけ飛ばして続行する。
    private static bool TryEnumerate(string path, out string[] directories, out string[] files)
    {
        directories = Array.Empty<string>();
        files = Array.Empty<string>();
        if (!Directory.Exists(path))
            return false;

        try
        {
            directories = Directory.EnumerateDirectories(path).ToArray();
            files = Directory.EnumerateFiles(path).ToArray();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string directory)
    {
        try
        {
            return (new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
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

    public void NotifySelected(string fullPath) => _workspace.SelectedPath = fullPath;

    public void NotifyActivated(string fullPath)
    {
        _workspace.SelectedPath = fullPath;
        if (File.Exists(fullPath))
            FileActivated?.Invoke(this, fullPath);
    }

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

    private void RefreshGitState()
    {
        if (_currentRoot is null)
        {
            _gitState = GitTreeState.Empty;
            FilterStatus = "";
            return;
        }

        _gitState = GitTreeState.Load(_currentRoot);

        var filters = new List<string>();
        if (HideIgnoredFiles) filters.Add("ignore 非表示");
        if (ShowChangedOnly) filters.Add("変更のみ");

        var gitStatus = _gitState.IsGitRepository
            ? $"{_gitState.ChangedFiles.Count} 件変更"
            : "Git 未検出";
        FilterStatus = filters.Count == 0
            ? gitStatus
            : $"{string.Join(" / ", filters)} - {gitStatus}";
    }

    private void StartWatching(string path)
    {
        _watcher?.Dispose();
        _refreshTimer?.Dispose();

        if (!Directory.Exists(path))
            return;

        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.Attributes
        };

        _watcher.Changed += OnWorkspaceChanged;
        _watcher.Created += OnWorkspaceChanged;
        _watcher.Deleted += OnWorkspaceChanged;
        _watcher.Renamed += OnWorkspaceChanged;
        _watcher.Error += (_, _) => ScheduleRefresh();
        _watcher.EnableRaisingEvents = true;
    }

    private void OnWorkspaceChanged(object sender, FileSystemEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        _refreshTimer ??= new System.Threading.Timer(_ =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                RefreshWorkspace();
                return;
            }

            if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
                return;

            dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    try
                    {
                        RefreshWorkspace();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _refreshQueued, 0);
                    }
                }));
        });

        _refreshTimer.Change(300, System.Threading.Timeout.Infinite);
    }
}

// ツリー上の差分マーク（FileNodeViewModel.GitStatus）の種別。表示文字・色は XAML 側の
// DataTrigger で割り当てる。DirectoryChanged は「配下に変更を含むフォルダ」を表す集約マーク。
public enum GitChangeKind
{
    None,
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted,
    DirectoryChanged,
}

internal sealed class GitTreeState
{
    private readonly string _rootPath;
    private readonly string? _gitRootPath;

    public static GitTreeState Empty { get; } = new("", null, new(), new(), new());

    public bool IsGitRepository => _gitRootPath is not null;

    /// <summary>変更ファイルのフルパス → 変更種別。</summary>
    public Dictionary<string, GitChangeKind> FileStatuses { get; }
    public HashSet<string> ChangedFiles { get; }
    public HashSet<string> ChangedDirectories { get; }

    private GitTreeState(
        string rootPath,
        string? gitRootPath,
        Dictionary<string, GitChangeKind> fileStatuses,
        HashSet<string> changedFiles,
        HashSet<string> changedDirectories)
    {
        _rootPath = rootPath;
        _gitRootPath = gitRootPath;
        FileStatuses = fileStatuses;
        ChangedFiles = changedFiles;
        ChangedDirectories = changedDirectories;
    }

    /// <summary>指定ファイルの変更種別（変更なしは None）。</summary>
    public GitChangeKind GetFileStatus(string fullPath)
        => FileStatuses.TryGetValue(Path.GetFullPath(fullPath), out var kind) ? kind : GitChangeKind.None;

    public static GitTreeState Load(string rootPath)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var gitRoot = FindGitRoot(fullRoot);
        if (gitRoot is null)
            return new GitTreeState(fullRoot, null, new(), new(), new());

        var fileStatuses = LoadFileStatuses(gitRoot);
        var changedFiles = new HashSet<string>(fileStatuses.Keys, StringComparer.OrdinalIgnoreCase);
        var changedDirectories = LoadChangedDirectories(changedFiles, fullRoot);
        return new GitTreeState(fullRoot, gitRoot, fileStatuses, changedFiles, changedDirectories);
    }

    public HashSet<string> GetIgnoredPaths(IEnumerable<string> fullPaths)
    {
        var ignoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedPaths = fullPaths.Select(Path.GetFullPath).ToArray();

        foreach (var path in normalizedPaths.Where(IsInsideGitDirectory))
            ignoredPaths.Add(path);

        if (_gitRootPath is null)
            return ignoredPaths;

        var candidates = normalizedPaths
            .Where(path => !ignoredPaths.Contains(path))
            .Select(path => new
            {
                FullPath = path,
                RelativePath = ToGitRelativePath(_gitRootPath, path)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.RelativePath))
            .ToArray();

        if (candidates.Length == 0)
            return ignoredPaths;

        var input = string.Join('\0', candidates.Select(x => x.RelativePath)) + '\0';
        var result = RunGit(_gitRootPath, input, new[] { "check-ignore", "-z", "--stdin" });
        if (result.ExitCode is not 0 and not 1)
            return ignoredPaths;

        foreach (var relativePath in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            ignoredPaths.Add(Path.GetFullPath(Path.Combine(_gitRootPath, relativePath)));

        return ignoredPaths;
    }

    private bool IsInsideGitDirectory(string fullPath)
    {
        var gitDir = Path.Combine(_gitRootPath ?? _rootPath, ".git");
        var normalizedGitDir = Path.GetFullPath(gitDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return fullPath.Equals(normalizedGitDir, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(normalizedGitDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindGitRoot(string rootPath)
    {
        var result = RunGit(rootPath, "rev-parse", "--show-toplevel");
        if (result.ExitCode != 0)
            return FindGitRootByDirectory(rootPath);

        var path = result.Output.Trim();
        return string.IsNullOrWhiteSpace(path)
            ? FindGitRootByDirectory(rootPath)
            : Path.GetFullPath(path);
    }

    private static string? FindGitRootByDirectory(string rootPath)
    {
        var directory = new DirectoryInfo(rootPath);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static Dictionary<string, GitChangeKind> LoadFileStatuses(string gitRoot)
    {
        var statuses = new Dictionary<string, GitChangeKind>(StringComparer.OrdinalIgnoreCase);

        var result = RunGit(gitRoot, "status", "--porcelain", "-z", "--untracked-files=all");
        if (result.ExitCode != 0)
            return statuses;

        var entries = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4)
                continue;

            var status = entry[..2];
            var relativePath = entry[3..];

            // リネーム/コピーは -z 形式では「新パス\0旧パス」の順で出力される。
            // 新パスは entry 側に含まれているので、続く旧パスのエントリを読み飛ばすだけにする
            // （旧パスはディスク上に存在せず、新パス＝追加扱いのファイルが表示対象）。
            if ((status[0] is 'R' or 'C' || status[1] is 'R' or 'C') && i + 1 < entries.Length)
                i++;

            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            statuses[Path.GetFullPath(Path.Combine(gitRoot, relativePath))] = MapStatus(status);
        }

        return statuses;
    }

    // git status --porcelain の XY 2 文字コードを表示用の種別へ落とす。
    // 競合（U を含む、AA/DD）を最優先で判定し、以降は X か Y のどちらかに現れた文字で分類する。
    private static GitChangeKind MapStatus(string xy)
    {
        if (xy == "??")
            return GitChangeKind.Untracked;

        var x = xy[0];
        var y = xy[1];

        if (x is 'U' || y is 'U' || xy is "AA" or "DD")
            return GitChangeKind.Conflicted;
        if (x is 'R' || y is 'R')
            return GitChangeKind.Renamed;
        if (x is 'A' || y is 'A')
            return GitChangeKind.Added;
        if (x is 'D' || y is 'D')
            return GitChangeKind.Deleted;
        return GitChangeKind.Modified;
    }

    private static HashSet<string> LoadChangedDirectories(HashSet<string> changedFiles, string visibleRoot)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedRoot = Path.GetFullPath(visibleRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var file in changedFiles)
        {
            var directory = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(directory))
            {
                var normalizedDirectory = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (normalizedDirectory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    break;

                if (!normalizedDirectory.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    break;

                directories.Add(normalizedDirectory);
                directory = Path.GetDirectoryName(normalizedDirectory);
            }
        }

        return directories;
    }

    private static string ToGitRelativePath(string gitRoot, string fullPath)
    {
        var relativePath = Path.GetRelativePath(gitRoot, fullPath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static GitCommandResult RunGit(string workingDirectory, params string[] args)
        => RunGit(workingDirectory, standardInput: null, args);

    // 注意: stdin 版の args は params にしない。params にすると
    // RunGit(wd, "status", "--porcelain", ...) のような呼び出しで "status" が
    // standardInput に解決され（git のサブコマンドが渡らず exit 129 になる）。
    // 明示的な string[] にすることで、可変長引数の呼び出しは必ず上の params 版へ振り分けられる。
    private static GitCommandResult RunGit(string workingDirectory, string? standardInput, string[] args)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();

            if (standardInput is not null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return new GitCommandResult(-1, "");
            }

            return new GitCommandResult(process.ExitCode, outputTask.GetAwaiter().GetResult());
        }
        catch
        {
            return new GitCommandResult(-1, "");
        }
    }

    private readonly record struct GitCommandResult(int ExitCode, string Output);
}

public sealed partial class FileNodeViewModel : ObservableObject
{
    private readonly FolderTreeViewModel _owner;
    private bool _loaded;

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public string Glyph => IsDirectory ? "📁" : "📄";

    public ObservableCollection<FileNodeViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    // git の差分マーク。XAML 側の DataTrigger が種別ごとに表示文字・色を割り当てる。
    [ObservableProperty] private GitChangeKind _gitStatus;

    public FileNodeViewModel(string fullPath, bool isDirectory, FolderTreeViewModel owner)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(fullPath);
        _owner = owner;
        GitStatus = owner.GitStatusFor(fullPath, isDirectory);
        if (isDirectory) Children.Add(Placeholder); // 遅延読込用ダミー
    }

    // 監視更新で git 状態が変わったとき、既存ノード（差分更新で再利用されるインスタンス）の
    // マークを最新へ更新する。
    public void RefreshGitStatus() => GitStatus = _owner.GitStatusFor(FullPath, IsDirectory);

    private static readonly FileNodeViewModel Placeholder = new();
    private FileNodeViewModel() { FullPath = ""; Name = ""; IsDirectory = false; _owner = null!; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && IsDirectory && !_loaded)
        {
            _loaded = true;
            Children.Clear();
            foreach (var child in _owner.Children(FullPath))
                Children.Add(child);
        }
    }

    // 畳まれた枝を遅延読込前の状態へ戻す。監視更新で中身が古くなっても、次に展開したとき
    // 最新を読み直すため、ダミーの子だけを残して再読込可能にする。
    public void ResetToLazy()
    {
        if (!IsDirectory || !_loaded)
            return;

        _loaded = false;
        Children.Clear();
        Children.Add(Placeholder);
    }

    // フィルタ済みの子を先に流し込み、遅延読込を無効化する（展開しても再読込しない）。
    public void LoadChildren(IReadOnlyList<FileNodeViewModel> children)
    {
        _loaded = true;
        Children.Clear();
        foreach (var child in children)
            Children.Add(child);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _owner.NotifySelected(FullPath);
    }
}
