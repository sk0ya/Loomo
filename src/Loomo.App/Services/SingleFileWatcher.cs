using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// <b>1つのファイルだけ</b>を見張る監視。<see cref="FileSystemWatcher"/> は単一ファイルを直接
/// 監視できないので、そのファイルの<b>親ディレクトリ</b>を <c>IncludeSubdirectories=false</c> ＋
/// <c>Filter=ファイル名</c> で張る。
/// <para>
/// <b>再帰監視は絶対にしない。</b>このリポジトリは再帰監視で実際に足を撃っている——
/// <see cref="DebouncedFolderWatcher"/> のコメントにあるとおり、<c>.git</c> 配下まで見張ると
/// <c>git status</c> が <c>.git/index</c> を書き換えるたびに監視が発火して再読込が走り、
/// また git を読む、という自己フィードバックループで UI スレッドを刻み続けた。1ファイルの表示のために
/// リポジトリ全体を見張るのは、その危険を負ったうえに隠れている間もずっと払う無駄でもある。
/// </para>
/// <para>
/// 発火はスレッドプールなので、通知は必ず生成時の <c>Dispatcher</c> 経由で UI スレッドへ渡す。
/// エディタの保存は「切り詰め→書き込み→属性更新」のように複数のイベントへ割れるので、
/// 既定 <see cref="DefaultDebounce"/> でまとめて1回にする。
/// </para>
/// </summary>
public sealed class SingleFileWatcher : IDisposable
{
    /// <summary>既定のデバウンス。1回の保存が複数の書き込みイベントに割れるのを1回へ畳む。</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    private readonly Action<string> _changed;
    private readonly TimeSpan _debounce;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private string? _pending;
    private bool _disposed;

    /// <param name="changed">変更が確定したときに UI スレッドで呼ばれる（引数は変更のあったフルパス）。</param>
    /// <param name="debounce">まとめる時間。null なら <see cref="DefaultDebounce"/>。</param>
    /// <param name="post">UI スレッドへ渡す手段。null なら生成時のディスパッチャ。
    /// テストは<b>同期に実行する</b>ものを渡して、ディスパッチャの動いていない場でも通知を受け取る。</param>
    public SingleFileWatcher(Action<string> changed, TimeSpan? debounce = null, Action<Action>? post = null)
    {
        _changed = changed;
        _debounce = debounce ?? DefaultDebounce;
        var dispatcher = Application.Current?.Dispatcher
            ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _post = post ?? (action => dispatcher.BeginInvoke(action));
    }

    /// <summary>いま見張っているファイルのフルパス（null＝見張っていない）。
    /// 「張りっぱなしになっていないか」を外から確かめられるようにしてある。</summary>
    public string? Target { get; private set; }

    /// <summary>実際に <see cref="FileSystemWatcher"/> を張った回数。<b>同じ対象では作り直していない</b>ことを
    /// 外から確かめるための目印——描画のたびに作り直すと、その入れ替わりの瞬間の変更を取りこぼす。</summary>
    internal int WatchGeneration { get; private set; }

    /// <summary>
    /// 見張る対象を張り替える。null・空・親フォルダーが無いものを渡すと見張りを止める。
    /// <b>同じ対象なら何もしない</b>——描画のたびに <see cref="FileSystemWatcher"/> を作り直さないため。
    /// </summary>
    public void Watch(string? filePath)
    {
        if (_disposed)
            return;

        var target = ToFullPath(filePath);
        if (string.Equals(target, Target, StringComparison.OrdinalIgnoreCase))
            return;

        Stop();
        if (target is null)
            return;

        var folder = Path.GetDirectoryName(target);
        var name = Path.GetFileName(target);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name) || !Directory.Exists(folder))
            return;

        try
        {
            var watcher = new FileSystemWatcher(folder, name)
            {
                // 親フォルダーを張るが、見るのはこの1ファイルだけ（再帰監視はしない・上のコメント）。
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.FileName
                    | NotifyFilters.CreationTime
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;   // 書き換えが「削除→作成」で来るエディタがある
            watcher.Renamed += OnFileEvent;   // 一時ファイルへ書いて置き換える保存もこちらで届く
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            Target = target;
            WatchGeneration++;
        }
        catch
        {
            // 監視できない場所（権限・ネットワーク・上限）は黙って諦める＝自動更新が効かないだけで、
            // 手で開き直せば従来どおり表示できる。
            Stop();
        }
    }

    /// <summary>見張りを止める（保留中のデバウンスも捨てる）。</summary>
    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _pending = null;
        }
        Target = null;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => HandleChange(e.FullPath);

    /// <summary>
    /// 変更1件をデバウンスへ入れる。<b>スレッドプールから呼ばれる前提</b>（監視の発火がそうなので）。
    /// テストは監視の実発火を待たずにここを直接叩けるようにしてある。
    /// </summary>
    internal void HandleChange(string fullPath)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _pending = fullPath;
            _timer ??= new Timer(_ => Flush());
            _timer.Change((int)_debounce.TotalMilliseconds, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        string? path;
        lock (_gate)
        {
            if (_disposed)
                return;
            path = _pending;
            _pending = null;
        }
        if (path is null)
            return;
        _post(() => _changed(path));
    }

    private static string? ToFullPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        try { return Path.GetFullPath(filePath); }
        catch { return null; }
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        Stop();
    }
}
