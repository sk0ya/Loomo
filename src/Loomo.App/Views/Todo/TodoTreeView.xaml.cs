namespace sk0ya.Loomo.App.Views;

/// <summary>表示中だけ監視する。ファイル変更はまとめて UI スレッドから再検索する。</summary>
public partial class TodoTreeView : UserControl
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly DispatcherTimer _timer;
    private TodoTreeViewModel? _model;
    private int _dirty;
    private bool _updating;
    public TodoTreeView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += OnTick;
        IsVisibleChanged += (_, _) => UpdateMonitoring();
        DataContextChanged += (_, _) => UpdateMonitoring();
        Loaded += (_, _) => UpdateMonitoring();
        Unloaded += (_, _) => StopMonitoring();
    }
    private void UpdateMonitoring()
    {
        StopMonitoring();
        if (!IsLoaded || !IsVisible || DataContext is not TodoTreeViewModel model) return;
        _model = model;
        model.Workspace.FoldersChanged += OnFoldersChanged;
        model.Invalidated += OnInvalidated;
        StartWatchers();
        Interlocked.Exchange(ref _dirty, 1);
        _timer.Start();
        OnTick(this, EventArgs.Empty);
    }
    private void StartWatchers()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        if (_model is null) return;
        foreach (var root in _model.Workspace.Folders)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                watcher.Renamed += OnFileChanged;
                watcher.Error += (_, _) => Interlocked.Exchange(ref _dirty, 1);
                _watchers.Add(watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (IOException) { /* 手動の再検索は引き続き利用できる */ }
            catch (UnauthorizedAccessException) { /* アクセス可能な他のフォルダーは監視を続ける */ }
        }
    }
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // ビルド出力で検索し続けない。検索結果の除外は検索サービスが担当する。
        if (e.FullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is ".git" or "bin" or "obj" or "node_modules")) return;
        Interlocked.Exchange(ref _dirty, 1);
    }
    private void OnInvalidated(object? sender, EventArgs e) => Interlocked.Exchange(ref _dirty, 1);
    private void OnFoldersChanged(object? sender, EventArgs e)
        => Dispatcher.InvokeAsync(() => { _model?.Invalidate(); StartWatchers(); });
    private async void OnTick(object? sender, EventArgs e)
    {
        if (_updating || _model is null || Interlocked.Exchange(ref _dirty, 0) == 0) return;
        _updating = true;
        try { await _model.RefreshCommand.ExecuteAsync(null); }
        finally { _updating = false; }
    }
    private void StopMonitoring()
    {
        _timer.Stop();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        if (_model is not null)
        {
            _model.Workspace.FoldersChanged -= OnFoldersChanged;
            _model.Invalidated -= OnInvalidated;
            _model.CancelSearch();
            _model = null;
        }
    }
    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TodoEntry entry && !_updating) _model?.OpenCommand.Execute(entry);
    }
    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TodoTree.SelectedItem is TodoEntry entry)
        { _model?.OpenCommand.Execute(entry); e.Handled = true; }
    }
}
