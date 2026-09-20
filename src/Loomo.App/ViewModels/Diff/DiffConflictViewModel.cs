using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>選択ファイルの競合解析、解決、undo、ステージを管理する。</summary>
public sealed partial class DiffConflictViewModel : ObservableObject
{
    private readonly DiffFileGateway _files;
    private readonly GitService _git;
    private readonly Action _clearDiff;
    private readonly Action<string, bool> _reportStatus;
    private readonly DiffConflictDisplayMapper _displayMapper = new();
    private DiffFileItem? _selectedFile;

    public DiffConflictViewModel(DiffFileGateway files, GitService git, Action clearDiff,
        Action<string, bool> reportStatus)
    {
        _files = files;
        _git = git;
        _clearDiff = clearDiff;
        _reportStatus = reportStatus;
    }
    [ObservableProperty] private bool _isConflictMode;

    public bool ShowDiffBody => !IsConflictMode;

    [ObservableProperty] private bool _isWholeFileConflict;

    [ObservableProperty] private bool _canMarkResolved;

    [ObservableProperty] private string _conflictProgressText = "";

    [ObservableProperty] private string _conflictPositionText = "";

    private int _conflictTotalCount;

    private int _conflictCursor = -1;

    public ObservableCollection<object> ConflictBlocks { get; } = new();

    [ObservableProperty] private string _conflictOursHeader = "";
    [ObservableProperty] private string _conflictTheirsHeader = "";

    [ObservableProperty] private string? _wholeFileOurs;
    [ObservableProperty] private string? _wholeFileTheirs;

    private ParsedConflictFile? _conflictParsed;

    private readonly Stack<(string Raw, int RegionIndex)> _conflictUndoStack = new();

    private string? _conflictUndoPath;

    private string? _conflictRawText;

    [ObservableProperty] private bool _canUndoResolve;

    [RelayCommand]
    private void NextConflict() => MoveConflictCursor(+1);

    [RelayCommand]
    private void PreviousConflict() => MoveConflictCursor(-1);

    public event Action<int>? ScrollToConflictRequested;

    private void MoveConflictCursor(int delta)
    {
        var regions = ConflictBlocks.OfType<ConflictRegionVm>().ToList();
        if (regions.Count == 0) return;
        _conflictCursor = ((_conflictCursor + delta) % regions.Count + regions.Count) % regions.Count;
        SetCurrentConflict(regions, _conflictCursor);
    }

    private void FocusFirstConflict()
    {
        var regions = ConflictBlocks.OfType<ConflictRegionVm>().ToList();
        if (regions.Count == 0) { ConflictPositionText = ""; return; }
        _conflictCursor = 0;
        SetCurrentConflict(regions, 0);
    }

    private void FocusConflictNear(int resolvedRegionIndex)
    {
        var regions = ConflictBlocks.OfType<ConflictRegionVm>().ToList();
        if (regions.Count == 0)
        {
            _conflictCursor = -1;
            ConflictPositionText = "";
            return;
        }
        var next = regions.FindIndex(r => r.Index > resolvedRegionIndex);
        _conflictCursor = next >= 0 ? next : regions.Count - 1;
        SetCurrentConflict(regions, _conflictCursor);
    }

    private void SetCurrentConflict(IReadOnlyList<ConflictRegionVm> regions, int cursor)
    {
        var current = regions[cursor];
        foreach (var r in regions) r.IsCurrent = ReferenceEquals(r, current);
        ConflictPositionText = $"{cursor + 1} / {regions.Count}";
        ScrollToConflictRequested?.Invoke(current.Index);
    }

    public void FocusConflictRegion(ConflictRegionVm region)
    {
        var regions = ConflictBlocks.OfType<ConflictRegionVm>().ToList();
        var index = regions.IndexOf(region);
        if (index < 0) return;
        _conflictCursor = index;
        SetCurrentConflict(regions, index);
    }

    private ConflictRegionVm? CurrentConflictRegion() =>
        ConflictBlocks.OfType<ConflictRegionVm>().FirstOrDefault(r => r.IsCurrent);

    [RelayCommand]
    private Task AcceptCurrentOursAsync() => AcceptRegionAsync(CurrentConflictRegion(), ConflictResolution.Ours);

    [RelayCommand]
    private Task AcceptCurrentTheirsAsync() => AcceptRegionAsync(CurrentConflictRegion(), ConflictResolution.Theirs);

    [RelayCommand]
    private Task AcceptCurrentBothAsync() => AcceptRegionAsync(CurrentConflictRegion(), ConflictResolution.Both);

    [RelayCommand]
    private Task ApplyCurrentResultAsync() => ApplyResultTextAsync(CurrentConflictRegion());

    public async Task LoadAsync(DiffFileItem? item, Func<DiffFileItem?, Task> loadDiff)
    {
        _selectedFile = item;
        if (item?.Entry?.IsConflicted == true)
        {
            IsConflictMode = true;
            await LoadConflictAsync(item);
        }
        else
        {
            IsConflictMode = false;
            ResetConflictState();
            await loadDiff(item);
        }
    }

    private void ResetConflictState()
    {
        IsWholeFileConflict = false;
        CanMarkResolved = false;
        ConflictProgressText = "";
        ConflictPositionText = "";
        _conflictCursor = -1;
        ConflictOursHeader = "";
        ConflictTheirsHeader = "";
        _conflictTotalCount = 0;
        _conflictParsed = null;
        _conflictUndoStack.Clear();
        _conflictUndoPath = null;
        _conflictRawText = null;
        CanUndoResolve = false;
        ConflictBlocks.Clear();
    }

    partial void OnIsConflictModeChanged(bool value) => OnPropertyChanged(nameof(ShowDiffBody));

    private async Task LoadConflictAsync(DiffFileItem item)
    {
        _clearDiff();

        string raw;
        try
        {
            raw = await _files.ReadTextAsync(item.FullPath);
        }
        catch (Exception ex)
        {
            IsWholeFileConflict = false;
            _conflictParsed = null;
            ConflictBlocks.Clear();
            SetStatus($"ファイルを読み込めませんでした: {ex.Message}", isError: true);
            return;
        }

        if (!string.Equals(item.FullPath, _conflictUndoPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(raw, _conflictRawText, StringComparison.Ordinal))
        {
            _conflictUndoStack.Clear();
        }
        _conflictUndoPath = item.FullPath;
        _conflictRawText = raw;
        CanUndoResolve = _conflictUndoStack.Count > 0;

        var parsed = ConflictMarkerParser.Parse(raw);
        if (parsed.HasConflicts)
        {
            IsWholeFileConflict = false;
            _conflictParsed = parsed;
            _conflictTotalCount = parsed.Regions.Count(r => r.Kind == ConflictRegionKind.Conflict);
            RebuildConflictDisplay(parsed);
            FocusFirstConflict();
            return;
        }

        _conflictParsed = null;
        ConflictBlocks.Clear();
        IsWholeFileConflict = true;
        CanMarkResolved = false;
        var (_, ours, theirs) = await _git.GetConflictSidesAsync(item.Entry!.Path);
        WholeFileOurs = ours;
        WholeFileTheirs = theirs;
    }

    private void RebuildConflictDisplay(ParsedConflictFile parsed)
    {
        var display = _displayMapper.Map(parsed);
        ConflictBlocks.Clear();
        foreach (var block in display.Blocks) ConflictBlocks.Add(block);
        ConflictOursHeader = display.OursHeader;
        ConflictTheirsHeader = display.TheirsHeader;
        CanMarkResolved = display.Remaining == 0;
        ConflictProgressText = _conflictTotalCount == 0 ? "" : $"残り {display.Remaining}/{_conflictTotalCount} 件";
    }

    [RelayCommand]
    private Task AcceptOursAsync(ConflictRegionVm? region) => AcceptRegionAsync(region, ConflictResolution.Ours);

    [RelayCommand]
    private Task AcceptTheirsAsync(ConflictRegionVm? region) => AcceptRegionAsync(region, ConflictResolution.Theirs);

    [RelayCommand]
    private Task AcceptBothAsync(ConflictRegionVm? region) => AcceptRegionAsync(region, ConflictResolution.Both);

    [RelayCommand]
    private async Task ApplyResultTextAsync(ConflictRegionVm? region)
    {
        if (region is null || _conflictParsed is null) return;
        if (_selectedFile is not { Entry: not null } item) return;

        var lines = region.ResultText.Length == 0
            ? Array.Empty<string>()
            : region.ResultText.Replace("\r\n", "\n").Split('\n');

        string resolved;
        try
        {
            resolved = ConflictMarkerParser.ResolveRegionWithLines(_conflictParsed, region.Index, lines);
        }
        catch (ArgumentException)
        {
            return; // リージョン構成が既に変わっている（多重クリック等）
        }

        await WriteResolvedAsync(item, resolved, region.Index);
    }

    private async Task AcceptRegionAsync(ConflictRegionVm? region, ConflictResolution resolution)
    {
        if (region is null || _conflictParsed is null) return;
        if (_selectedFile is not { Entry: not null } item) return;

        string resolved;
        try
        {
            resolved = ConflictMarkerParser.ResolveRegion(_conflictParsed, region.Index, resolution);
        }
        catch (ArgumentException)
        {
            return; // リージョン構成が既に変わっている（多重クリック等）
        }

        await WriteResolvedAsync(item, resolved, region.Index);
    }

    private async Task WriteResolvedAsync(DiffFileItem item, string resolved, int resolvedRegionIndex)
    {
        var previous = _conflictRawText;
        try
        {
            await _files.WriteTextAsync(item.FullPath, resolved);
        }
        catch (Exception ex)
        {
            SetStatus($"書き込みに失敗しました: {ex.Message}", isError: true);
            return;
        }

        if (previous is not null)
        {
            _conflictUndoStack.Push((previous, resolvedRegionIndex));
            CanUndoResolve = true;
        }
        _conflictUndoPath = item.FullPath;
        _conflictRawText = resolved;

        var reparsed = ConflictMarkerParser.Parse(resolved);
        _conflictParsed = reparsed;
        RebuildConflictDisplay(reparsed);
        FocusConflictNear(resolvedRegionIndex);
    }

    [RelayCommand]
    private async Task UndoResolveAsync()
    {
        if (_conflictUndoStack.Count == 0) return;
        if (_selectedFile is not { Entry: not null } item) return;
        if (!string.Equals(item.FullPath, _conflictUndoPath, StringComparison.OrdinalIgnoreCase)) return;

        var (raw, regionIndex) = _conflictUndoStack.Peek();
        try
        {
            await _files.WriteTextAsync(item.FullPath, raw);
        }
        catch (Exception ex)
        {
            SetStatus($"書き込みに失敗しました: {ex.Message}", isError: true);
            return;
        }
        _conflictUndoStack.Pop();
        CanUndoResolve = _conflictUndoStack.Count > 0;
        _conflictRawText = raw;

        var reparsed = ConflictMarkerParser.Parse(raw);
        _conflictParsed = reparsed;
        RebuildConflictDisplay(reparsed);

        var regions = ConflictBlocks.OfType<ConflictRegionVm>().ToList();
        var cursor = regions.FindIndex(r => r.Index == regionIndex);
        if (regions.Count > 0)
        {
            _conflictCursor = cursor >= 0 ? cursor : 0;
            SetCurrentConflict(regions, _conflictCursor);
        }
    }

    [RelayCommand]
    private async Task MarkResolvedAsync()
    {
        if (!CanMarkResolved || _selectedFile is not { Entry: not null } item) return;
        var result = await _git.StageAsync(item.Entry.Path);
        SetStatus(result.Success
            ? $"{item.DisplayPath} を解決済みにしました。"
            : $"解決済みにできませんでした: {Truncate(result.Message)}", isError: !result.Success);
    }

    [RelayCommand]
    private Task AcceptWholeFileOursAsync() => AcceptWholeFileAsync(WholeFileOurs);

    [RelayCommand]
    private Task AcceptWholeFileTheirsAsync() => AcceptWholeFileAsync(WholeFileTheirs);

    private async Task AcceptWholeFileAsync(string? content)
    {
        if (content is null || _selectedFile is not { Entry: not null } item) return;
        try
        {
            await _files.WriteTextAsync(item.FullPath, content);
        }
        catch (Exception ex)
        {
            SetStatus($"書き込みに失敗しました: {ex.Message}", isError: true);
            return;
        }
        var result = await _git.StageAsync(item.Entry.Path);
        SetStatus(result.Success
            ? $"{item.DisplayPath} を解決済みにしました。"
            : $"解決済みにできませんでした: {Truncate(result.Message)}", isError: !result.Success);
    }

    [RelayCommand]
    private async Task DeleteFileConflictAsync()
    {
        if (_selectedFile is not { Entry: not null } item) return;
        try
        {
            _files.DeleteIfExists(item.FullPath);
        }
        catch (Exception ex)
        {
            SetStatus($"削除に失敗しました: {ex.Message}", isError: true);
            return;
        }
        var result = await _git.StageAsync(item.Entry.Path);
        SetStatus(result.Success
            ? $"{item.DisplayPath} を削除して解決済みにしました。"
            : $"解決済みにできませんでした: {Truncate(result.Message)}", isError: !result.Success);
    }
    private void SetStatus(string message, bool isError) => _reportStatus(message, isError);

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
