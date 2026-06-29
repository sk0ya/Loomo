using System;
using System.IO;
using System.Windows.Threading;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ワークスペース配下の <c>*.cs</c> 変更を監視し、遅延（デバウンス）付きで <see cref="Changed"/> を発火する。
/// テストエクスプローラの自動再収集に使う。監視できない（権限/パス）場合は無音で動く（手動契機は呼び出し側に残る）。</summary>
internal sealed class TestSourceWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;

    /// <summary>ソース変更が落ち着いた後に（UI スレッドで）発火する。</summary>
    public event Action? Changed;

    public TestSourceWatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>監視対象ルートを張り替える。</summary>
    public void Watch(string? root)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            var w = new FileSystemWatcher(root, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            };
            w.Changed += OnChanged;
            w.Created += OnChanged;
            w.Deleted += OnChanged;
            w.Renamed += OnChanged;
            w.Error += (_, _) => _dispatcher.InvokeAsync(Schedule);
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { /* 監視不可。自動更新なしでも探索自体は動く */ }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var sep = Path.DirectorySeparatorChar;
        var p = e.FullPath;
        // ビルド成果物配下の .cs（生成物・コピー）はテスト集合に影響しないので無視（ビルド中の連続通知も抑える）。
        if (p.Contains($"{sep}bin{sep}") || p.Contains($"{sep}obj{sep}") || p.Contains($"{sep}artifacts{sep}"))
            return;
        _dispatcher.InvokeAsync(Schedule);
    }

    private void Schedule()
    {
        _debounce ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        if (!_wired)
        {
            _debounce.Tick += (_, _) => { _debounce!.Stop(); Changed?.Invoke(); };
            _wired = true;
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private bool _wired;

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Stop();
    }
}
