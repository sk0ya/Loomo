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

    private void OnChanged(object sender, FileSystemEventArgs e) => ScheduleRefresh();

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
