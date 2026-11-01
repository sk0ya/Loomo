using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Files;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ワークスペースの全リポジトリの「いまの様子」を1か所に持つ（§40.6）。エクスプローラー・ファイル一覧
/// （と、§40.11 の後段で Git パネル・ステータスバー）は、git を自分で起こさずここから受け取る。
///
/// <para><b>読み込みの共有</b>：<see cref="Load"/> はリポジトリごとに直列で、頼まれた時刻より後に読み終えた状態が
/// あればそれを返す。ツリーの複数の表示ルートが同じリポジトリにかかっていても、ツリーとファイル一覧が同時に
/// 頼んでも、git は1回で済む。</para>
///
/// <para><b>監視</b>：読み込んだリポジトリの git ディレクトリ（と、ワークツリーなら共通ディレクトリ）の
/// <c>HEAD</c>・<c>index</c>・<c>refs/</c>・<c>packed-refs</c>・止まっている操作の印<b>だけ</b>を見る。ターミナルで
/// コミット・チェックアウト・fetch したのを、作業ツリーのファイルが変わらなくても拾うため。<c>objects/</c>・
/// <c>logs/</c>・<c>*.lock</c>・<c>FETCH_HEAD</c> は無視する（fetch で何千回も鳴る／refs の変化で足りる）。
/// 自分の status は <c>--no-optional-locks</c> なので index を書かず、監視が自分の読み込みで鳴ることはない。</para>
///
/// <para>作業ツリーの中身の変化は、今どおりエクスプローラー・ファイル一覧の <c>DebouncedFolderWatcher</c> が拾い、
/// 読み直しを頼む（<c>fresh: true</c>）。ここで作業ツリー全体を監視し直すことはしない。</para>
/// </summary>
public sealed class GitWorkspaceStatus : IDisposable
{
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(300);

    private readonly GitRepositoryCatalog _catalog;
    private readonly IWorkspaceService? _workspace;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, CancellationToken, GitRepositoryFileState> _loader;
    private bool _disposed;

    public GitWorkspaceStatus(GitRepositoryCatalog catalog, IWorkspaceService workspace)
        : this(catalog, workspace, GitRepositoryFileState.Load)
    {
    }

    internal GitWorkspaceStatus(
        GitRepositoryCatalog catalog,
        IWorkspaceService? workspace,
        Func<string, CancellationToken, GitRepositoryFileState> loader)
    {
        _catalog = catalog;
        _workspace = workspace;
        _loader = loader;
        _catalog.Changed += (_, _) => DropRemovedRepositories();
    }

    /// <summary>
    /// リポジトリの git ディレクトリが変わった（コミット・チェックアウト・ステージ・fetch・rebase…）。
    /// 引数は作業ツリーの根。<b>背景スレッド</b>で鳴る——受け手が UI へ回す。
    /// </summary>
    public event EventHandler<string>? RepositoryChanged;

    /// <summary>リポジトリの状態を読み終えた（中身が前と同じでも鳴る）。引数は作業ツリーの根。背景スレッドで鳴る。</summary>
    public event EventHandler<string>? StatusLoaded;

    /// <summary>全リポジトリ（<see cref="GitRepositoryCatalog.Repositories"/>）。</summary>
    public GitRepositoryCatalog Catalog => _catalog;

    /// <summary>読み込み済みの状態（無ければ null・git は起こさない）。</summary>
    public GitRepositoryFileState? Cached(string repositoryRoot)
    {
        lock (_gate)
            return _entries.TryGetValue(WorkspacePaths.Normalize(repositoryRoot), out var entry) ? entry.State : null;
    }

    /// <summary>
    /// リポジトリの状態を読む（同期・git を起こし得る）。UI スレッドから呼ばない。
    /// <paramref name="fresh"/> が false なら読み込み済みの状態をそのまま返す。true なら、呼んだ時刻より後に
    /// 読み終えた状態が無ければ読み直す（同時に頼まれた読み込みは1回にまとまる）。
    /// </summary>
    public GitRepositoryFileState Load(string repositoryRoot, bool fresh, CancellationToken token)
    {
        var root = WorkspacePaths.Normalize(repositoryRoot);
        var requested = DateTime.UtcNow;
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(root, out entry!))
                _entries[root] = entry = new Entry(root);
        }

        entry.LoadGate.Wait(token);
        try
        {
            var current = entry.State;
            if (current is not null && !entry.Stale && (!fresh || entry.LoadedAtUtc >= requested))
                return current;

            var state = _loader(root, token);
            lock (_gate)
            {
                entry.State = state;
                entry.LoadedAtUtc = DateTime.UtcNow;
                entry.Stale = false;
            }
            EnsureWatching(entry);
            StatusLoaded?.Invoke(this, root);
            return state;
        }
        finally
        {
            entry.LoadGate.Release();
        }
    }

    /// <summary>
    /// 表示ルート <paramref name="visibleRoot"/>（エクスプローラーのワークスペースフォルダー、またはピン留めした
    /// サブフォルダー）にかかるリポジトリ——先祖が根のもの、配下にあるもの——を読んで束ねる（同期）。
    /// </summary>
    internal GitTreeState TreeStateFor(string visibleRoot, bool fresh, CancellationToken token)
    {
        var root = WorkspacePaths.Normalize(visibleRoot);
        var states = new List<GitRepositoryFileState>();
        foreach (var repository in RepositoriesFor(root))
        {
            token.ThrowIfCancellationRequested();
            states.Add(Load(repository.Root, fresh, token));
        }
        return GitTreeState.Compose(root, states);
    }

    /// <summary>
    /// パスに出す状態（読み込み済みの状態だけを見る・git は起こさない）。所属リポジトリが未読込・どれにも
    /// 属さなければ None。フォルダーは「配下に変更あり」（作業ツリーの根の行を含む）。
    /// </summary>
    public GitChangeKind StatusForPath(string fullPath, bool isDirectory)
    {
        var repository = _catalog.RepositoryFor(fullPath);
        if (repository is null) return GitChangeKind.None;
        return Cached(repository.Root)?.GetStatus(fullPath, isDirectory) ?? GitChangeKind.None;
    }

    /// <summary>
    /// パスの集合の状態を、所属リポジトリごとに1回の <c>check-ignore</c> でまとめて出す（同期・UI スレッドから呼ばない）。
    /// 未読込のリポジトリは読み込み済みの状態があればそれを、無ければ読んでから答える。
    /// </summary>
    public IReadOnlyDictionary<string, GitChangeKind> StatusesForPaths(
        IEnumerable<(string FullPath, bool IsDirectory)> entries, CancellationToken token = default)
    {
        var result = new Dictionary<string, GitChangeKind>(StringComparer.OrdinalIgnoreCase);
        var grouped = new Dictionary<string, List<(string FullPath, bool IsDirectory)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var full = WorkspacePaths.Normalize(entry.FullPath);
            if (full.Length == 0) continue;
            if (_catalog.RepositoryFor(full) is not { } repository) continue;
            if (!grouped.TryGetValue(repository.Root, out var list))
                grouped[repository.Root] = list = new();
            list.Add((full, entry.IsDirectory));
        }

        foreach (var pair in grouped)
        {
            token.ThrowIfCancellationRequested();
            var state = Load(pair.Key, fresh: false, token);
            state.GetIgnoredPaths(pair.Value.Select(e => e.FullPath));
            foreach (var entry in pair.Value)
                result[entry.FullPath] = state.GetStatus(entry.FullPath, entry.IsDirectory);
        }
        return result;
    }

    /// <summary>表示ルートにかかるリポジトリ。一覧（カタログ）にあるものを使い、表示ルートがワークスペースの外
    /// （一覧が知らない場所）ならその場で探す。</summary>
    private IReadOnlyList<GitRepositoryInfo> RepositoriesFor(string visibleRoot)
    {
        var known = _catalog.Repositories
            .Where(r => WorkspacePaths.IsWithin(r.Root, visibleRoot) || WorkspacePaths.IsWithin(visibleRoot, r.Root))
            .ToArray();
        if (known.Length > 0) return known;
        var insideWorkspace = _workspace?.Contains(visibleRoot) ?? false;
        return insideWorkspace ? known : GitRepositoryCatalog.Discover(new[] { visibleRoot });
    }

    // ===== 監視 =====

    private void EnsureWatching(Entry entry)
    {
        lock (_gate)
        {
            if (_disposed || entry.Watchers is not null) return;
            entry.Watchers = new List<FileSystemWatcher>();
            var gitDirectory = GitRepositoryCatalog.ResolveGitDirectory(entry.Root);
            if (gitDirectory is null || !Directory.Exists(gitDirectory)) return;
            var directories = new List<string> { gitDirectory };
            var common = CommonDirectoryOf(gitDirectory);
            if (common is not null && !string.Equals(common, gitDirectory, StringComparison.OrdinalIgnoreCase))
                directories.Add(common);

            foreach (var directory in directories)
            {
                try
                {
                    var watcher = new FileSystemWatcher(directory)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Changed += (_, e) => OnGitDirectoryEvent(entry, directory, e.FullPath);
                    watcher.Created += (_, e) => OnGitDirectoryEvent(entry, directory, e.FullPath);
                    watcher.Deleted += (_, e) => OnGitDirectoryEvent(entry, directory, e.FullPath);
                    watcher.Renamed += (_, e) => OnGitDirectoryEvent(entry, directory, e.FullPath);
                    // バッファあふれ（大きな fetch・checkout）は「何か変わった」として読み直す。
                    watcher.Error += (_, _) => ScheduleChanged(entry);
                    watcher.EnableRaisingEvents = true;
                    entry.Watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    // 監視できない場所（ネットワークドライブの一部等）は、作業ツリー側の監視と手動更新に任せる。
                }
            }
        }
    }

    /// <summary>ワークツリーの git ディレクトリ（<c>&lt;common&gt;/worktrees/&lt;名前&gt;</c>）なら共通ディレクトリ。
    /// refs はそちらにある。</summary>
    private static string? CommonDirectoryOf(string gitDirectory)
    {
        var parent = Path.GetDirectoryName(gitDirectory);
        return parent is not null && Path.GetFileName(parent).Equals("worktrees", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(parent)
            : null;
    }

    private void OnGitDirectoryEvent(Entry entry, string watchedDirectory, string fullPath)
    {
        if (IsRelevantGitPath(watchedDirectory, fullPath))
            ScheduleChanged(entry);
    }

    /// <summary>git ディレクトリの中のどの変化を「様子が変わった」とみなすか（純関数・テスト用に internal）。</summary>
    internal static bool IsRelevantGitPath(string gitDirectory, string fullPath)
    {
        string relative;
        try { relative = Path.GetRelativePath(gitDirectory, fullPath); }
        catch (ArgumentException) { return false; }
        if (relative.StartsWith("..", StringComparison.Ordinal)) return false;
        relative = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        var first = relative.Split(Path.DirectorySeparatorChar)[0];
        if (relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        return first switch
        {
            "HEAD" or "index" or "packed-refs" or "refs"
                or "MERGE_HEAD" or "CHERRY_PICK_HEAD" or "REVERT_HEAD" or "BISECT_LOG"
                or "rebase-merge" or "rebase-apply" => true,
            // ワークツリーの共通ディレクトリを見ているとき、worktrees/<名前>/ の中はそのワークツリー自身の
            // git ディレクトリ（別の監視が見ている）——ここで拾うと別のワークツリーの変化で鳴る。
            _ => false,
        };
    }

    private void ScheduleChanged(Entry entry)
    {
        lock (_gate)
        {
            if (_disposed) return;
            entry.Stale = true;
            entry.DebounceTimer ??= new Timer(_ => RaiseChanged(entry));
            entry.DebounceTimer.Change(ChangeDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseChanged(Entry entry)
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        RepositoryChanged?.Invoke(this, entry.Root);
    }

    /// <summary>一覧から外れたリポジトリの監視を止め、状態を捨てる（ワークスペースの切替・フォルダーの削除）。</summary>
    private void DropRemovedRepositories()
    {
        // カタログの Changed は UI スレッドで鳴り得るので、一覧の作り直し（ディレクトリ列挙）は背景で。
        _ = Task.Run(() =>
        {
            HashSet<string> alive;
            try
            {
                alive = new HashSet<string>(_catalog.Repositories.Select(r => r.Root), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            List<Entry> removed;
            lock (_gate)
            {
                if (_disposed) return;
                // 一覧に無いものは捨てる。まだ使う面（ワークスペースの外を見ている等）があれば、次の Load で作り直される。
                removed = _entries.Values.Where(e => !alive.Contains(e.Root)).ToList();
                foreach (var entry in removed)
                    _entries.Remove(entry.Root);
            }
            foreach (var entry in removed)
                entry.Dispose();
        });
    }

    public void Dispose()
    {
        List<Entry> entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToList();
            _entries.Clear();
        }
        foreach (var entry in entries)
            entry.Dispose();
    }

    private sealed class Entry(string root) : IDisposable
    {
        public string Root { get; } = root;
        public SemaphoreSlim LoadGate { get; } = new(1, 1);
        public GitRepositoryFileState? State { get; set; }
        public DateTime LoadedAtUtc { get; set; }
        /// <summary>git ディレクトリが変わった後、まだ読み直していない。</summary>
        public bool Stale { get; set; }
        public List<FileSystemWatcher>? Watchers { get; set; }
        public Timer? DebounceTimer { get; set; }

        public void Dispose()
        {
            DebounceTimer?.Dispose();
            if (Watchers is not null)
            {
                foreach (var watcher in Watchers)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
            }
        }
    }
}
