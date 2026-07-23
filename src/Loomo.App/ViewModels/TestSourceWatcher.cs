using System;
using System.IO;
using System.Windows.Threading;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ワークスペース配下のソース変更を監視し、遅延（デバウンス）付きで <see cref="Changed"/> を発火する。
/// テストエクスプローラの自動再収集に使う。既定は <c>*.cs</c>（dotnet）、TypeScript 側はテストファイルの
/// パターンと node_modules 無視を渡す。監視できない（権限/パス）場合は無音で動く（手動契機は呼び出し側に残る）。</summary>
internal sealed class TestSourceWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly string[] _filters;
    private readonly string[] _ignoreDirs;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;

    /// <summary>ソース変更が落ち着いた後に（UI スレッドで）発火する。</summary>
    public event Action? Changed;

    /// <param name="filters">監視するファイルパターン（省略時は *.cs）。</param>
    /// <param name="ignoreDirs">無視するディレクトリ名（省略時はビルド成果物）。</param>
    public TestSourceWatcher(Dispatcher dispatcher, string[]? filters = null, string[]? ignoreDirs = null)
    {
        _dispatcher = dispatcher;
        _filters = filters is { Length: > 0 } ? filters : ["*.cs"];
        _ignoreDirs = ignoreDirs is { Length: > 0 } ? ignoreDirs : ["bin", "obj", "artifacts"];
    }

    /// <summary>監視対象ルートを張り替える。</summary>
    public void Watch(string? root)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            };
            foreach (var f in _filters) w.Filters.Add(f);
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
        // 生成物/依存ディレクトリ配下はテスト集合に影響しないので無視（ビルド/インストール中の連続通知も抑える）。
        foreach (var dir in _ignoreDirs)
            if (p.Contains($"{sep}{dir}{sep}"))
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
