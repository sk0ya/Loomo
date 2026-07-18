using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>Git コミット履歴のページング、スコープ、絞り込み、選択詳細を管理する。</summary>
public sealed partial class GitHistoryViewModel : ObservableObject
{
    private const int PageSize = 200;
    public const string AllAuthorsLabel = "（すべての作者）";
    private readonly GitSessionQuery _query;
    private string? _branch;
    private string? _path;
    private int _loadedCommitCount;
    private CommitLogFilter _parsedFilter = CommitLogFilter.Parse(null);

    [ObservableProperty] private GitLogRow? _selectedLogRow;
    [ObservableProperty] private string _commitDetail = "";
    [ObservableProperty] private bool _isPathScoped;
    [ObservableProperty] private string _pathScopeLabel = "";
    [ObservableProperty] private bool _isLogScoped;
    [ObservableProperty] private string _logFilter = "";
    [ObservableProperty] private IReadOnlyList<string> _authorOptions = new[] { AllAuthorsLabel };
    [ObservableProperty] private string _authorSelection = AllAuthorsLabel;
    [ObservableProperty] private DateTime? _dateFrom;
    [ObservableProperty] private DateTime? _dateTo;
    [ObservableProperty] private bool _hasMoreLog;
    [ObservableProperty] private bool _isLoadingMoreLog;

    public ObservableCollection<GitLogRow> LogRows { get; } = new();
    public System.ComponentModel.ICollectionView LogView { get; }
    private string? EffectiveAuthor => string.IsNullOrEmpty(AuthorSelection)
        || AuthorSelection == AllAuthorsLabel ? null : AuthorSelection;
    public bool HasActiveFilters => !_parsedFilter.IsEmpty || EffectiveAuthor is not null
        || DateFrom.HasValue || DateTo.HasValue;

    public GitHistoryViewModel(GitSessionQuery query)
    {
        _query = query;
        LogView = CollectionViewSource.GetDefaultView(LogRows);
        LogView.Filter = FilterLogRow;
    }

    partial void OnLogFilterChanged(string value)
    {
        _parsedFilter = CommitLogFilter.Parse(value);
        RefreshView();
    }
    partial void OnAuthorSelectionChanged(string value) => RefreshView();
    partial void OnDateFromChanged(DateTime? value) => RefreshView();
    partial void OnDateToChanged(DateTime? value) => RefreshView();
    partial void OnSelectedLogRowChanged(GitLogRow? value)
    {
        if (value?.Hash is { } hash) _ = LoadDetailAsync(hash);
    }

    [RelayCommand]
    private void ClearLogFilters()
    {
        LogFilter = "";
        AuthorSelection = AllAuthorsLabel;
        DateFrom = null;
        DateTo = null;
    }

    [RelayCommand]
    private void ClearDateFilter()
    {
        DateFrom = null;
        DateTo = null;
    }

    [RelayCommand]
    private Task ClearPathScope()
    {
        ResetPathScope();
        return ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        var selectedHash = SelectedLogRow?.Hash;
        LogRows.Clear();
        _loadedCommitCount = 0;
        SelectedLogRow = await AppendPageAsync(selectedHash);
        if (SelectedLogRow is null) CommitDetail = "";
        UpdateAuthorOptions();
    }

    public async Task LoadMoreAsync()
    {
        if (IsLoadingMoreLog || !HasMoreLog) return;
        IsLoadingMoreLog = true;
        try
        {
            await AppendPageAsync(SelectedLogRow?.Hash);
            UpdateAuthorOptions();
        }
        finally { IsLoadingMoreLog = false; }
    }

    public Task ShowBranchAsync(GitBranchInfo branch)
    {
        _branch = branch.Name;
        IsLogScoped = true;
        return ReloadAsync();
    }

    public Task ShowAllBranchesAsync()
    {
        _branch = null;
        IsLogScoped = false;
        return ReloadAsync();
    }

    public async Task ShowPathAsync(string root, string fullPath, string? selectHash = null)
    {
        _path = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        PathScopeLabel = _path;
        IsPathScoped = true;
        await ReloadAsync();
        if (!string.IsNullOrWhiteSpace(selectHash)) await SelectLoadedOrOlderAsync(selectHash);
    }

    public async Task SelectCommitAsync(string hash)
    {
        _branch = null;
        IsLogScoped = false;
        ResetPathScope();
        await ReloadAsync();
        await SelectLoadedOrOlderAsync(hash);
    }

    public void Clear()
    {
        LogRows.Clear();
        UpdateAuthorOptions();
        CommitDetail = "";
        ResetPathScope();
    }

    public void ResetPathScope()
    {
        _path = null;
        IsPathScoped = false;
        PathScopeLabel = "";
    }

    private async Task<GitLogRow?> AppendPageAsync(string? reselectHash)
    {
        var page = await _query.GetLogPageAsync(_branch, PageSize, _loadedCommitCount, _path);
        var count = 0;
        GitLogRow? reselect = null;
        foreach (var row in page)
        {
            LogRows.Add(row);
            if (row.IsCommit) { count++; _loadedCommitCount++; }
            if (reselectHash is not null && row.Hash == reselectHash) reselect = row;
        }
        HasMoreLog = count >= PageSize;
        return reselect;
    }

    private async Task SelectLoadedOrOlderAsync(string hash)
    {
        var target = FindCommitRow(hash);
        while (target is null && HasMoreLog)
        {
            await LoadMoreAsync();
            target = FindCommitRow(hash);
        }
        if (target is null) return;
        if (!LogView.Contains(target)) ClearLogFilters();
        SelectedLogRow = target;
    }

    private GitLogRow? FindCommitRow(string hash)
    {
        var sought = hash.Trim().TrimStart('^');
        return LogRows.FirstOrDefault(row => row is { IsCommit: true, Hash: { } candidate }
            && (string.Equals(candidate, sought, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(sought, StringComparison.OrdinalIgnoreCase)));
    }

    private bool FilterLogRow(object item)
    {
        if (!HasActiveFilters) return true;
        if (item is not GitLogRow { IsCommit: true } row || !_parsedFilter.Matches(row)) return false;
        if (EffectiveAuthor is { } author && !string.Equals(row.Author, author, StringComparison.Ordinal)) return false;
        if (!DateFrom.HasValue && !DateTo.HasValue) return true;
        var day = CommitLogFilter.DayOf(row);
        if (day is null) return false;
        return (DateFrom is not { } from || string.CompareOrdinal(day, from.ToString("yyyy-MM-dd")) >= 0)
            && (DateTo is not { } to || string.CompareOrdinal(day, to.ToString("yyyy-MM-dd")) <= 0);
    }

    private void RefreshView()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        LogView.Refresh();
    }

    private void UpdateAuthorOptions()
    {
        var authors = LogRows.Where(row => row.IsCommit && !string.IsNullOrEmpty(row.Author))
            .Select(row => row.Author!).Distinct(StringComparer.Ordinal)
            .OrderBy(author => author, StringComparer.CurrentCultureIgnoreCase).ToList();
        var options = new List<string>(authors.Count + 1) { AllAuthorsLabel };
        options.AddRange(authors);
        AuthorOptions = options;
        if (!options.Contains(AuthorSelection, StringComparer.Ordinal)) AuthorSelection = AllAuthorsLabel;
    }

    private async Task LoadDetailAsync(string hash) => CommitDetail = await _query.GetCommitSummaryAsync(hash);
}
