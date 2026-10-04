using sk0ya.Loomo.App.Services.Infrastructure;

namespace sk0ya.Loomo.App.Views;

/// <summary>表示中だけ監視する。ファイル変更はまとめて UI スレッドから再検索する。</summary>
public partial class TodoTreeView : UserControl
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly DispatcherTimer _timer;
    private TodoTreeViewModel? _model;
    private int _dirty;
    private bool _updating;
    private bool _suppressPreview;
    private TodoEntry? _pendingPreview;
    private readonly DispatcherTimer _previewTimer;
    private TodoTreeViewModel? Vm => DataContext as TodoTreeViewModel;
    public TodoTreeView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += OnTick;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _previewTimer.Tick += (_, _) => {
            _previewTimer.Stop();
            if (_pendingPreview is { } entry && ReferenceEquals(Vm?.SelectedEntry, entry) && IsVisible)
                Vm.PreviewCommand.Execute(entry);
            _pendingPreview = null;
        };
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
        model.SelectionRequested += OnSelectionRequested;
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
        CancelPreview();
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        if (_model is not null)
        {
            _model.Workspace.FoldersChanged -= OnFoldersChanged;
            _model.Invalidated -= OnInvalidated;
            _model.SelectionRequested -= OnSelectionRequested;
            _model.CancelSearch();
            _model = null;
        }
    }
    private void CancelPreview()
    {
        _previewTimer.Stop();
        _pendingPreview = null;
    }
    private void QueuePreview(TodoEntry entry)
    {
        CancelPreview();
        _pendingPreview = entry;
        _previewTimer.Start();
    }
    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Vm is not { } vm || vm.IsUpdatingResults) return;
        vm.SetSelection(e.NewValue as TodoEntry);
        if (e.NewValue is TodoEntry entry && !_suppressPreview && TodoTree.IsKeyboardFocusWithin)
            QueuePreview(entry);
        else CancelPreview();
    }
    private void OnSelectionRequested(object? sender, TodoEntry entry)
    {
        CancelPreview();
        _suppressPreview = true;
        try
        {
            if (Vm is null) return;
            ItemsControl parent = TodoTree;
            foreach (var node in TodoTreeLayout.PathTo(Vm.TreeItems, entry))
            {
                if (node is TodoFolder folder) folder.IsExpanded = true;
                if (node is TodoGroup group) group.IsExpanded = true;
                var container = RealizeItem(parent, parent.Items.IndexOf(node));
                if (container is null) return;
                if (node is TodoEntry) { container.IsSelected = true; container.BringIntoView(); container.Focus(); }
                parent = container;
            }
        }
        finally { _suppressPreview = false; }
    }
    /// <summary>画面外の項目も実体化してから選択する（F8 でスクロール外へ進んだ場合）。</summary>
    private static TreeViewItem? RealizeItem(ItemsControl owner, int index)
    {
        owner.ApplyTemplate();
        owner.UpdateLayout();
        if (owner.ItemContainerGenerator.ContainerFromIndex(index) is TreeViewItem existing) return existing;
        WpfTreeTraversal.FindDescendant<VirtualizingStackPanel>(owner)?.BringIndexIntoViewPublic(index);
        owner.UpdateLayout();
        return owner.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
    }
    public void FocusSelectedEntry()
    {
        if (Vm?.SelectedEntry is { } entry) OnSelectionRequested(this, entry);
    }
    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        var container = WpfTreeTraversal.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not TodoEntry entry) return;
        if (e.ClickCount == 2)
        {
            CancelPreview();
            Vm?.OpenCommand.Execute(entry);
            e.Handled = true;
        }
        else if (container.IsSelected) QueuePreview(entry);
    }
    private void OnGroupClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not (TodoGroup or TodoFolder)) return;
        CancelPreview();
        if (e.ClickCount == 1)
        {
            if (element.DataContext is TodoGroup group) group.IsExpanded = !group.IsExpanded;
            if (element.DataContext is TodoFolder folder) folder.IsExpanded = !folder.IsExpanded;
        }
        WpfTreeTraversal.FindAncestor<TreeViewItem>(element)?.Focus();
        e.Handled = true;
    }
    private void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        { ShowFilter(); e.Handled = true; }
        else if (e.Key == Key.F5) { vm.RefreshCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F8)
        {
            CancelPreview();
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) vm.PreviousCommand.Execute(null);
            else vm.NextCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsFilterVisible)
        { CloseFilter(); e.Handled = true; }
        else if (e.Key == Key.Enter && ExcludeBox.IsKeyboardFocusWithin)
        { ApplyExclusion(); e.Handled = true; }
        else if (TodoTree.IsKeyboardFocusWithin && TodoTree.SelectedItem is TodoEntry entry)
        {
            if (e.Key == Key.Enter) { CancelPreview(); vm.OpenCommand.Execute(entry); e.Handled = true; }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            { ClipboardText.Set(entry.Hit.LineText.Trim()); e.Handled = true; }
        }
    }
    private void ShowFilter()
    {
        if (Vm is null) return;
        Vm.IsFilterVisible = true;
        FilterBox.Focus();
        FilterBox.SelectAll();
    }
    private void CloseFilter()
    {
        if (Vm is null) return;
        Vm.Filter = "";
        Vm.IsFilterVisible = false;
        TodoTree.Focus();
    }
    private void OnToggleFilter(object sender, RoutedEventArgs e)
    { if (Vm?.IsFilterVisible == true) CloseFilter(); else ShowFilter(); }
    private void OnCloseFilter(object sender, RoutedEventArgs e) => CloseFilter();
    private void OnCloseExclusion(object sender, RoutedEventArgs e)
    {
        ExcludeBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        if (Vm is { } vm) vm.IsExclusionVisible = false;
    }
    private void ApplyExclusion() => ExcludeBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    private void OnApplyExclusion(object sender, RoutedEventArgs e) => ApplyExclusion();

    private static MenuItem AddMenu(ContextMenu menu, string text, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = text, InputGestureText = gesture ?? "" };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not FrameworkElement target) return;
        CancelPreview();
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        AddMenu(menu, "フォルダー階層で表示", () => vm.GroupByTag = false).IsChecked = !vm.GroupByTag;
        AddMenu(menu, "タグ別に表示", () => vm.GroupByTag = true).IsChecked = vm.GroupByTag;
        menu.Items.Add(new Separator());
        AddMenu(menu, "すべて展開", () => vm.ExpandAllCommand.Execute(null));
        AddMenu(menu, "すべて折りたたむ", () => vm.CollapseAllCommand.Execute(null));
        AddMenu(menu, "表示中の TODO をコピー", () => vm.CopyResultsCommand.Execute(null)).IsEnabled = vm.HasEntries;
        menu.Items.Add(new Separator());
        AddMenu(menu, "絞り込みを解除", () => vm.ClearFiltersCommand.Execute(null)).IsEnabled = vm.IsFiltered;
        AddMenu(menu, "除外するパスを設定…", () => {
            vm.IsExclusionVisible = true;
            ExcludeBox.Focus();
        }).IsChecked = vm.HasExclusion;
        target.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
    {
        CancelPreview();
        _suppressPreview = true;
        try
        {
            var container = WpfTreeTraversal.FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
            if (container is not null) { container.IsSelected = true; container.Focus(); }
        }
        finally { _suppressPreview = false; }
    }
    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Vm is not { } vm) { e.Handled = true; return; }
        CancelPreview();
        var menu = new ContextMenu();
        if (TodoTree.SelectedItem is TodoEntry entry)
        {
            AddMenu(menu, "プレビュー", () => vm.PreviewCommand.Execute(entry));
            AddMenu(menu, "タブで開く", () => vm.OpenCommand.Execute(entry), "Enter");
            menu.Items.Add(new Separator());
            AddMenu(menu, "内容をコピー", () => ClipboardText.Set(entry.Hit.LineText.Trim()), "Ctrl+C");
            AddMenu(menu, "相対パスと行番号をコピー", () => ClipboardText.Set(entry.Location));
            AddMenu(menu, "絶対パスをコピー", () => ClipboardText.Set(entry.Hit.FullPath));
            AddMenu(menu, "エクスプローラーで表示", () => FileExplorerLauncher.RevealInExplorer(entry.Hit.FullPath));
            menu.Items.Add(new Separator());
            AddMenu(menu, $"{entry.Tag} だけを表示", () => vm.ShowOnlyTagCommand.Execute(entry.Tag));
        }
        else if (TodoTree.SelectedItem is TodoFolder folder)
        {
            AddMenu(menu, folder.IsExpanded ? "折りたたむ" : "展開する", () => folder.IsExpanded = !folder.IsExpanded);
            AddMenu(menu, "このフォルダーの TODO をコピー", () => ClipboardText.Set(string.Join(Environment.NewLine,
                folder.Entries.Select(item => $"{item.Location} [{item.Tag}] {item.Body}"))));
            AddMenu(menu, "フォルダーのパスをコピー", () => ClipboardText.Set(folder.FullPath));
            AddMenu(menu, "エクスプローラーで開く", () => FileExplorerLauncher.OpenInExplorer(folder.FullPath));
        }
        else if (TodoTree.SelectedItem is TodoGroup group)
        {
            AddMenu(menu, group.IsExpanded ? "折りたたむ" : "展開する", () => group.IsExpanded = !group.IsExpanded);
            AddMenu(menu, "このグループの TODO をコピー", () => ClipboardText.Set(string.Join(Environment.NewLine,
                group.Entries.Select(item => $"{item.Location} [{item.Tag}] {item.Body}"))));
        }
        else { e.Handled = true; return; }
        TodoTree.ContextMenu = menu;
    }
}
