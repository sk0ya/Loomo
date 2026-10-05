using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>TODO の検索・表示条件・選択。自動更新では同じ行とグループを再利用する。</summary>
public sealed partial class TodoTreeViewModel : ObservableObject, IDisposable
{
    public const string TagPattern = @"\b(TODO|FIXME|HACK|NOTE)\b";
    public const int ResultLimit = 2000;
    private static readonly Regex Tags = new(TagPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly ITodoSearchService _search;
    private readonly IWorkspaceService _workspace;
    private readonly LoomoSettings? _settings;
    private readonly SettingsStore? _settingsStore;
    private readonly Dictionary<string, bool> _expansion = new();
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<TodoEntry> _entries = [];
    private IReadOnlyList<TodoEntry> _visibleEntries = [];
    private bool _truncated;
    private bool _disposed;
    private bool _applyingSearchOptions;
    private string? _error;
    private string? _notice;
    private TodoEntry? _selectedEntry;

    public TodoTreeViewModel(ITodoSearchService search, IWorkspaceService workspace,
        LoomoSettings? settings = null, SettingsStore? settingsStore = null)
    {
        _search = search;
        _workspace = workspace;
        _settings = settings;
        _settingsStore = settingsStore;
        _groupByTag = settings?.TodoTree.GroupByTag ?? false;
        _excludeGlob = settings?.TodoTree.ExcludeGlob ?? "";
        CodeExtensions = settings?.TodoTree.CodeExtensions ?? TodoTreeSettings.DefaultCodeExtensions;
        DocumentExtensions = settings?.TodoTree.DocumentExtensions ?? "";
        foreach (var tag in TagFilters)
        {
            tag.IsEnabled = settings?.TodoTree.HiddenTags.Contains(tag.Tag) != true;
            tag.PropertyChanged += (_, e) => {
                if (e.PropertyName != nameof(TodoTagFilter.IsEnabled)) return;
                RebuildGroups();
                Persist();
            };
        }
        FileIcons.PaletteChanged += OnIconPaletteChanged;
    }

    // 明暗テーマの切替でアイコンの配色が変わるので、表示中の行に引き直させる。
    private void OnIconPaletteChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        foreach (var folder in TodoTreeLayout.Folders(TreeItems)) folder.RefreshIcon();
        foreach (var group in Groups) group.RefreshIcon();
    }
    public ObservableCollection<TodoGroup> Groups { get; } = [];
    public ObservableCollection<object> TreeItems { get; } = [];
    public IReadOnlyList<TodoTagFilter> TagFilters { get; } =
        [new("TODO"), new("FIXME"), new("HACK"), new("NOTE")];
    public event EventHandler<ContentSearchHit>? OpenRequested;
    public event EventHandler<ContentSearchHit>? PreviewRequested;
    public event EventHandler<TodoEntry>? SelectionRequested;
    public event EventHandler? Invalidated;
    public IWorkspaceService Workspace => _workspace;
    public TodoEntry? SelectedEntry => _selectedEntry;
    public bool IsUpdatingResults { get; private set; }
    public int VisibleCount => _visibleEntries.Count;
    public bool HasEntries => VisibleCount > 0;
    public bool IsEmpty => !IsBusy && !HasEntries;
    public bool IsFiltered => Filter.Length > 0 || TagFilters.Any(t => !t.IsEnabled);
    public bool HasExclusion => !string.IsNullOrWhiteSpace(ExcludeGlob);
    public string CountLabel => IsFiltered ? $"{VisibleCount} / {_entries.Count}" : $"{VisibleCount}";
    public string PositionLabel => _selectedEntry is { } entry && _visibleEntries.ToList().IndexOf(entry) is var i && i >= 0
        ? $"{i + 1} / {VisibleCount}" : $"{VisibleCount} 件";
    public string EmptyTitle => _error is not null ? "検索できませんでした"
        : _workspace.Folders.Count == 0 ? "フォルダーを開いてください"
        : IsFiltered || HasExclusion ? "条件に一致する TODO がありません" : "TODO は見つかりませんでした";
    public string EmptyDescription => _error ?? (_workspace.Folders.Count == 0
        ? "開いたフォルダーの TODO をここに表示します。"
        : IsFiltered || HasExclusion ? "絞り込みや除外条件を解除して確認できます。"
        : "指定した拡張子のコメント内を検索します。対象は「…」の検索設定で変更できます。");

    public string CodeExtensions { get; private set; }
    public string DocumentExtensions { get; private set; }
    [ObservableProperty] private string _searchOptionsError = "";
    public bool ApplySearchOptions(string code, string documents, string exclude)
    {
        try { TodoSearchQuery.Validate(code, documents); }
        catch (ArgumentException ex) { SearchOptionsError = ex.Message; return false; }
        CodeExtensions = string.Join(" ", TodoSearchQuery.ParseExtensions(code));
        DocumentExtensions = string.Join(" ", TodoSearchQuery.ParseExtensions(documents));
        _applyingSearchOptions = true;
        try { ExcludeGlob = exclude.Trim(); }
        finally { _applyingSearchOptions = false; }
        OnPropertyChanged(nameof(CodeExtensions));
        OnPropertyChanged(nameof(DocumentExtensions));
        OnPropertyChanged(nameof(ExcludeGlob));
        SearchOptionsError = "";
        Invalidate();
        Persist();
        return true;
    }

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _excludeGlob = "";
    [ObservableProperty] private bool _groupByTag;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isFilterVisible;
    [ObservableProperty] private bool _isExclusionVisible;
    [ObservableProperty] private string _status = "保存済みファイルを検索します";
    partial void OnFilterChanged(string value) => RebuildGroups();
    partial void OnGroupByTagChanged(bool value) { RebuildGroups(); Persist(); }
    partial void OnExcludeGlobChanged(string value) { if (!_applyingSearchOptions) { Invalidate(); Persist(); } }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    public void Invalidate()
    {
        CancelSearch();
        _entries = [];
        _truncated = false;
        _notice = null;
        _error = null;
        RebuildGroups();
        Invalidated?.Invoke(this, EventArgs.Empty);
    }
    public void CancelSearch() => _searchCancellation?.Cancel();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RefreshAsync()
    {
        if (_disposed) return;
        CancelSearch();
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        IsBusy = true;
        _error = null;
        Status = "検索中…";
        try
        {
            var options = new TodoSearchOptions(CodeExtensions, DocumentExtensions,
                string.IsNullOrWhiteSpace(ExcludeGlob) ? null : ExcludeGlob.Trim(), ResultLimit);
            var result = await Task.Run(() => _search.SearchAsync(options, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || _disposed) return;
            _truncated = result.Truncated;
            _notice = result.Notice;
            var old = _entries.ToDictionary(e => e.Key);
            _entries = result.Entries.Select(e => old.TryGetValue(e.Key, out var previous)
                && previous.Hit == e.Hit && previous.ContentEndColumn == e.ContentEndColumn ? previous : e).ToList();
            RebuildGroups();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
            { _error = ex.Message; Status = $"検索できませんでした: {ex.Message}"; NotifyState(); }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            { _searchCancellation = null; IsBusy = false; }
        }
    }

    public static IReadOnlyList<TodoEntry> Parse(IEnumerable<ContentSearchHit> hits)
        => hits.SelectMany(hit => Tags.Matches(hit.LineText).Cast<Match>()
            .Select(match => new TodoEntry(match.Value, hit with { Column = match.Index + 1 })))
            .DistinctBy(entry => entry.Key).ToList();

    private void RebuildGroups()
    {
        IsUpdatingResults = true;
        try
        {
            foreach (var group in Groups) _expansion[group.Key] = group.IsExpanded;
            var oldFolders = TodoTreeLayout.Folders(TreeItems).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in oldFolders.Values)
                foreach (var path in folder.Paths) _expansion[$"folder:{path}"] = folder.IsExpanded;
            var filter = Filter.Trim();
            var matching = _entries.Where(e => filter.Length == 0 || e.Hit.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || e.Hit.LineText.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var tag in TagFilters) tag.Count = matching.Count(e => e.Tag == tag.Tag);
            var enabled = TagFilters.Where(t => t.IsEnabled).Select(t => t.Tag).ToHashSet();
            var entries = matching.Where(e => enabled.Contains(e.Tag)).ToList();
            var existing = Groups.ToDictionary(g => g.Key);
            var groups = new List<TodoGroup>();
            foreach (var grouping in entries.GroupBy(e => GroupByTag ? e.Tag : e.Hit.FullPath)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var sorted = grouping.OrderBy(e => e.Hit.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Hit.Line).ThenBy(e => e.Hit.Column).ToList();
                var group = new TodoGroup(GroupByTag ? grouping.Key : sorted[0].Hit.RelativePath, [], GroupByTag, grouping.Key);
                if (existing.TryGetValue(group.Key, out var previous)) group = previous;
                group.IsExpanded = !_expansion.TryGetValue(group.Key, out var expanded) || expanded;
                Reconcile(group.Entries, sorted);
                group.NotifyCount();
                groups.Add(group);
            }
            Reconcile(Groups, groups);
            var tree = GroupByTag ? groups.Cast<object>().ToList() : TodoTreeLayout.Build(groups, _workspace);
            Reconcile(TreeItems, tree.Select(ReuseFolder).ToList());
            _visibleEntries = TodoTreeLayout.Entries(TreeItems).ToList();

            object ReuseFolder(object node)
            {
                if (node is not TodoFolder desired) return node;
                var folder = oldFolders.TryGetValue(desired.Key, out var previous) && previous.Name == desired.Name ? previous : desired;
                folder.IsExpanded = desired.Paths.All(path => !_expansion.TryGetValue($"folder:{path}", out var expanded) || expanded);
                var children = desired.Children.Select(ReuseFolder).ToList();
                Reconcile(folder.Children, children);
                folder.NotifyCount();
                return folder;
            }
            // フィルターや更新で消えた行は、別の行を勝手に開かず選択だけを解除する。
            SetSelection(_visibleEntries.FirstOrDefault(e => e.Key == _selectedEntry?.Key));
            Status = _workspace.Folders.Count == 0 ? "フォルダー未選択"
                : $"{VisibleCount} 件・{entries.Select(e => e.Hit.FullPath).Distinct().Count()} ファイル";
            if (HasExclusion) Status += "・除外あり";
            if (_truncated) Status += $"（先頭 {ResultLimit} 件まで）";
            if (_notice is not null) Status += "・" + _notice;
            NotifyState();
        }
        finally { IsUpdatingResults = false; }
    }

    private static void Reconcile<T>(ObservableCollection<T> collection, IReadOnlyList<T> wanted)
    {
        var keep = wanted.ToHashSet();
        for (var i = collection.Count - 1; i >= 0; i--)
            if (!keep.Contains(collection[i])) collection.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < collection.Count && EqualityComparer<T>.Default.Equals(collection[i], wanted[i])) continue;
            var previous = collection.IndexOf(wanted[i]);
            if (previous >= 0) collection.Move(previous, i);
            else collection.Insert(i, wanted[i]);
        }
    }

    public void SetSelection(TodoEntry? entry)
    {
        if (_selectedEntry != entry && _selectedEntry is not null) _selectedEntry.IsSelected = false;
        _selectedEntry = entry;
        if (entry is not null) entry.IsSelected = true;
        OnPropertyChanged(nameof(SelectedEntry));
        OnPropertyChanged(nameof(PositionLabel));
    }
    private void NotifyState()
    {
        foreach (var property in new[] { nameof(VisibleCount), nameof(HasEntries), nameof(IsEmpty), nameof(IsFiltered),
            nameof(HasExclusion), nameof(CountLabel), nameof(PositionLabel), nameof(EmptyTitle), nameof(EmptyDescription) })
            OnPropertyChanged(property);
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand] private void Open(TodoEntry? entry)
    { if (entry is not null) { SetSelection(entry); OpenRequested?.Invoke(this, entry.Hit); } }
    [RelayCommand] private void Preview(TodoEntry? entry)
    { if (entry is not null) { SetSelection(entry); PreviewRequested?.Invoke(this, entry.Hit); } }
    [RelayCommand(CanExecute = nameof(HasEntries))] private void Next() => MoveSelection(1);
    [RelayCommand(CanExecute = nameof(HasEntries))] private void Previous() => MoveSelection(-1);
    private void MoveSelection(int direction)
    {
        if (!HasEntries) return;
        var index = _selectedEntry is null ? -1 : _visibleEntries.ToList().IndexOf(_selectedEntry);
        var next = index < 0 ? direction > 0 ? 0 : VisibleCount - 1 : (index + direction + VisibleCount) % VisibleCount;
        var entry = _visibleEntries[next];
        foreach (var ancestor in TodoTreeLayout.PathTo(TreeItems, entry))
        {
            if (ancestor is TodoFolder folder) folder.IsExpanded = true;
            if (ancestor is TodoGroup group) group.IsExpanded = true;
        }
        SetSelection(entry);
        SelectionRequested?.Invoke(this, entry);
        PreviewRequested?.Invoke(this, entry.Hit);
    }
    [RelayCommand] private void ExpandAll() => SetAllExpanded(true);
    [RelayCommand] private void CollapseAll() => SetAllExpanded(false);
    private void SetAllExpanded(bool expanded)
    {
        foreach (var folder in TodoTreeLayout.Folders(TreeItems)) folder.IsExpanded = expanded;
        foreach (var group in Groups) group.IsExpanded = expanded;
    }
    [RelayCommand] private void ClearFilters()
    {
        Filter = "";
        foreach (var tag in TagFilters) tag.IsEnabled = true;
    }
    [RelayCommand] private void ResetConditions() { ClearFilters(); ExcludeGlob = ""; }
    [RelayCommand] private void ShowOnlyTag(string? tag)
    { foreach (var item in TagFilters) item.IsEnabled = item.Tag == tag; }
    [RelayCommand] private void CopyResults()
        => ClipboardText.Set(string.Join(Environment.NewLine, _visibleEntries.Select(e => $"{e.Location} [{e.Tag}] {e.Body}")));
    private void Persist()
    {
        if (_settings is null) return;
        _settings.TodoTree.GroupByTag = GroupByTag;
        _settings.TodoTree.ExcludeGlob = ExcludeGlob;
        _settings.TodoTree.CodeExtensions = CodeExtensions;
        _settings.TodoTree.DocumentExtensions = DocumentExtensions;
        _settings.TodoTree.HiddenTags = TagFilters.Where(t => !t.IsEnabled).Select(t => t.Tag).ToList();
        try { _settingsStore?.Save(_settings); }
        catch (Exception ex) { Status = $"表示設定を保存できませんでした: {ex.Message}"; }
    }
    public void Dispose() { _disposed = true; FileIcons.PaletteChanged -= OnIconPaletteChanged; CancelSearch(); }
}
