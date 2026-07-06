using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// FolderTree 用のファイル監視。<see cref="FileSystemWatcher"/> のイベントをデバウンス（300ms）し、
/// UI スレッド（Dispatcher）上で 1 回だけ refresh コールバックを呼ぶ。監視は bin/obj/.git 等の
/// 更新で頻発するため、Background 優先度でキューし多重実行は抑止する。
/// </summary>
public sealed class DebouncedFolderWatcher : IDisposable
{
    private readonly Action _refresh;
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private int _refreshQueued;

    /// <param name="refresh">変更検知後に UI スレッドで呼ばれる再読込処理。</param>
    public DebouncedFolderWatcher(Action refresh) => _refresh = refresh;

    /// <summary>監視先を切り替える（既存の監視・保留中のデバウンスは破棄）。</summary>
    public void Watch(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        _timer?.Dispose();
        _timer = null;

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

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Error += (_, _) => ScheduleRefresh();
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>再帰監視で無視するディレクトリ（パスのどこかにこのセグメントを含む変更は refresh しない）。
    /// とくに <c>.git</c> は致命的で、git 状態の読込（<c>git status</c>）が <c>.git/index</c> を書き換える
    /// たびに監視が発火し、また git 読込が走る自己フィードバックループになって UI スレッドを刻み続ける。
    /// <c>bin</c>/<c>obj</c> 等はビルドで頻繁に更新されるだけでツリー表示に無関係なので併せて外す。</summary>
    private static readonly string[] IgnoredSegments =
        { ".git", "bin", "obj", "node_modules", ".vs", ".idea" };

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (IsIgnoredPath(e.FullPath))
            return;
        ScheduleRefresh();
    }

    /// <summary>変更パスが無視対象ディレクトリの下か（ディレクトリセグメント単位の一致で判定）。</summary>
    private static bool IsIgnoredPath(string fullPath)
    {
        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            foreach (var ignored in IgnoredSegments)
                if (segment.Equals(ignored, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private void ScheduleRefresh()
    {
        _timer ??= new Timer(_ =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                _refresh();
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
                        _refresh();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _refreshQueued, 0);
                    }
                }));
        });

        _timer.Change(300, Timeout.Infinite);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _timer?.Dispose();
        _timer = null;
    }
}
