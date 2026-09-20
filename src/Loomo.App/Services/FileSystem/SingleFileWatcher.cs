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
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly Action<string> _changed;
    private readonly TimeSpan _debounce;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private Timer? _retryTimer;
    private string? _pending;
    // 親フォルダーが一時的に消えていても、再接続するために要求中の対象は保持する。
    private string? _requestedTarget;
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
    /// 見張る対象を張り替える。null・空なら見張りを止める。親フォルダーが一時的に無い場合は
    /// 見張りを止めた状態で再接続を予約し、フォルダーが戻れば自動で張り直す。
    /// <b>同じ対象なら何もしない</b>——描画のたびに <see cref="FileSystemWatcher"/> を作り直さないため。
    /// </summary>
    public void Watch(string? filePath)
    {
        var target = ToFullPath(filePath);
        lock (_gate)
        {
            if (_disposed
                || string.Equals(target, _requestedTarget, StringComparison.OrdinalIgnoreCase))
                return;

            _requestedTarget = target;
            StopCore();
            if (target is not null)
                TryAttachCore(target);
        }
    }

    /// <summary>見張りを止める（保留中のデバウンスも捨てる）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _requestedTarget = null;
            StopCore();
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => HandleChange(e.FullPath);

    private void OnWatcherError(object sender, ErrorEventArgs e) => HandleWatcherError(sender);

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

    /// <summary>
    /// 監視そのものが死んだとき（内部バッファ溢れ・ネットワーク共有の切断・親フォルダーの消失）に
    /// <b>同じ対象へ張り直す</b>。ここを繋がないと、以後そのファイルを保存しても二度と届かないのに
    /// <see cref="Target"/> は張ったままに見える＝自動更新が黙って死ぬ。
    /// <para><b>Stop() を挟むのが要点</b>——<see cref="Watch"/> は「同じ対象なら何もしない」で短絡するので、
    /// <c>Target</c> を残したまま呼んでも復活しない。</para>
    /// 監視の発火と同じくスレッドプールから来る前提で、テストからも直接叩けるようにしてある。
    /// </summary>
    internal void HandleWatcherError()
    {
        HandleWatcherError(source: null);
    }

    private void HandleWatcherError(object? source)
    {
        lock (_gate)
        {
            if (_disposed || (source is not null && !ReferenceEquals(source, _watcher)))
                return;

            var target = _requestedTarget;
            if (target is null)
                return;

            // Watch/Stop と同じロック内で現行の監視だけを外し、要求中の対象へ再接続する。
            // 親フォルダーがまだ戻っていなければ TryAttachCore が再試行を予約する。
            StopCore();
            TryAttachCore(target);
        }
    }

    /// <summary>_gate を保持したまま現在のWatcher・デバウンス・再接続予約を破棄する。</summary>
    private void StopCore()
    {
        var watcher = _watcher;
        _watcher = null;
        Target = null;
        watcher?.Dispose();

        _timer?.Dispose();
        _timer = null;
        _pending = null;

        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    /// <summary>_gate を保持したまま、要求中の対象へWatcherを張る。失敗時は再試行する。</summary>
    private void TryAttachCore(string target)
    {
        var folder = Path.GetDirectoryName(target);
        var name = Path.GetFileName(target);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name) || !Directory.Exists(folder))
        {
            ScheduleRetryCore();
            return;
        }

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(folder, name)
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
            watcher.Error += OnWatcherError;  // 監視そのものが死ぬ経路（下の HandleWatcherError）
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            Target = target;
            WatchGeneration++;
        }
        catch
        {
            // 監視できない場所（権限・ネットワーク・上限）は一時的な可能性があるため、再試行する。
            watcher?.Dispose();
            ScheduleRetryCore();
        }
    }

    /// <summary>_gate を保持したまま、親フォルダー復旧後の再接続を予約する。</summary>
    private void ScheduleRetryCore()
    {
        if (_disposed || _requestedTarget is null)
            return;

        _retryTimer ??= new Timer(_ => RetryAttach(), null, Timeout.Infinite, Timeout.Infinite);
        _retryTimer.Change(RetryDelay, Timeout.InfiniteTimeSpan);
    }

    private void RetryAttach()
    {
        lock (_gate)
        {
            var retryTimer = _retryTimer;
            _retryTimer = null;
            retryTimer?.Dispose();

            if (_disposed || _watcher is not null || _requestedTarget is null)
                return;
            TryAttachCore(_requestedTarget);
        }
    }

    private void Flush()
    {
        // _post まで _gate の内側でやる。外に出すと「_disposed を見た直後に破棄された」隙間で
        // 終了中のディスパッチャへ投げてしまう——閉じ際の1件のために落ちる経路を残さない。
        lock (_gate)
        {
            if (_disposed)
                return;
            var path = _pending;
            _pending = null;
            if (path is null)
                return;
            try
            {
                _post(() => _changed(path));
            }
            catch (InvalidOperationException)
            {
                // 終了処理に入ったディスパッチャは BeginInvoke を拒む。閉じている最中の更新1件を
                // 落とすだけのことで、プロセスごと道連れにする理由にはならない。
            }
        }
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
        {
            _disposed = true;
            _requestedTarget = null;
            StopCore();
        }
    }
}
