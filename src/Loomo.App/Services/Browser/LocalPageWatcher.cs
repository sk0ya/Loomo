using System.IO;
using System.Threading;

namespace sk0ya.Loomo.App.Services;

/// <summary>ブラウザペインで開いているローカルのページを、保存したら読み直すための見張り（§24.45）。
/// <para>
/// 見張るのは<b>そのページが実際に読んだファイルだけ</b>（<see cref="LocalFileServer.ServedFiles"/>）。
/// ページのフォルダーを再帰で見張ると、リポジトリの直下の <c>index.html</c> 1つのために <c>.git</c> まで
/// 拾って刻み続ける（<see cref="SingleFileWatcher"/> の注意書きと同じ理由）。ファイルの親フォルダーを
/// 非再帰で張り、届いた変更が読んだファイルのどれかであるときだけ知らせる。
/// </para>
/// </summary>
internal sealed class LocalPageWatcher : IDisposable
{
    /// <summary>張るフォルダーの上限。部品を大量に読むページでも監視を際限なく増やさない。</summary>
    public const int MaxFolders = 8;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly Action _changed;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private bool _disposed;

    /// <param name="changed">読んだファイルのどれかが変わったとき、UI スレッドで呼ばれる。</param>
    public LocalPageWatcher(Action changed, Action<Action>? post = null)
    {
        _changed = changed;
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _post = post ?? (action => dispatcher.BeginInvoke(action));
    }

    /// <summary>いま見張っているファイル（テストと「張りっぱなしでないか」の確認用）。</summary>
    public IReadOnlyCollection<string> Files
    {
        get { lock (_gate) return _files.ToArray(); }
    }

    /// <summary>見張る相手を張り替える。空なら止める。同じ集合なら何もしない（張り替えの隙に変更を取りこぼさない）。</summary>
    public void Watch(IReadOnlyCollection<string> files)
    {
        lock (_gate)
        {
            if (_disposed || (_files.SetEquals(files) && (files.Count == 0 || _watchers.Count > 0)))
                return;
            StopCore();
            _files = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in FoldersOf(_files))
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    };
                    watcher.Changed += OnEvent;
                    watcher.Created += OnEvent;   // 「削除→作成」で書き換えるエディタがある
                    watcher.Renamed += OnRenamed; // 一時ファイルへ書いて置き換える保存
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch { /* 見張れないフォルダー（権限・ネットワーク）は諦める。手での再読み込みは残る */ }
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
            _files = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>読んだファイルから張るフォルダー（重複を除き、上限まで。ページ自身＝先頭のフォルダーを先に）。</summary>
    internal static IReadOnlyList<string> FoldersOf(IEnumerable<string> files)
        => files.Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFolders)
            .ToList();

    /// <summary>届いた変更を数えるか。読んだファイルそのものだけ（同じフォルダーの別ファイルは数えない）。</summary>
    internal static bool Matches(IReadOnlySet<string> files, string changedPath)
        => files.Contains(changedPath);

    private void OnEvent(object sender, FileSystemEventArgs e) => HandleChange(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        HandleChange(e.FullPath);
        HandleChange(e.OldFullPath);
    }

    internal void HandleChange(string fullPath)
    {
        lock (_gate)
        {
            if (_disposed || !Matches(_files, fullPath))
                return;
            _timer ??= new Timer(_ => _post(Fire));
            _timer.Change((int)Debounce.TotalMilliseconds, Timeout.Infinite);
        }
    }

    private void Fire()
    {
        lock (_gate)
            if (_disposed || _files.Count == 0)
                return;
        _changed();
    }

    private void StopCore()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopCore();
        }
    }
}
