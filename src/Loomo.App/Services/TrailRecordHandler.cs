using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

public sealed record TrailRecordRequest(
    TrailEntryKind Kind, string Target, string Label, int Line, int Column,
    DisplayMode DisplayMode, PaneKind? StagePane, string? PaneLayout);

public sealed record TrailRecordResult(TrailEntryViewModel Entry, bool Added, bool LiveDayChanged);

/// <summary>軌跡のデデュープ、永続化、最新地点更新を担当する Command Handler。</summary>
public sealed class TrailRecordHandler
{
    private readonly TrailStore _store;
    private readonly Func<DateTime> _now;
    private string _workspaceKey = "";
    private DateOnly _liveDay;

    public TrailRecordHandler(TrailStore store, Func<DateTime> now)
    {
        _store = store;
        _now = now;
        _liveDay = DateOnly.FromDateTime(now());
    }

    public TrailEntryViewModel? Latest { get; private set; }
    public string? LatestFileTarget => Latest is { Kind: TrailEntryKind.File } entry ? entry.Target : null;

    public void SetWorkspace(string workspaceKey)
    {
        _workspaceKey = workspaceKey;
        Latest = null;
    }

    public void SetLatest(DateOnly liveDay, TrailEntryViewModel? latest)
    {
        _liveDay = liveDay;
        Latest = latest;
    }

    public TrailRecordResult? Record(TrailRecordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Target)) return null;
        var now = _now();
        var today = DateOnly.FromDateTime(now);
        var dayChanged = today != _liveDay;
        if (dayChanged)
        {
            _liveDay = today;
            Latest = null;
        }

        var comparison = request.Kind is TrailEntryKind.File or TrailEntryKind.Edit or TrailEntryKind.Preview
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Latest is { } last && last.Kind == request.Kind
            && string.Equals(last.Target, request.Target, comparison)
            && last.Mode == request.DisplayMode && last.StagePane == request.StagePane
            && (request.Kind is not TrailEntryKind.Edit || last.Line == request.Line))
        {
            last.Label = request.Label;
            last.Timestamp = now;
            last.PaneLayout = request.PaneLayout;
            if (request.Line >= 0) { last.Line = request.Line; last.Column = request.Column; }
            if (last.Id >= 0)
                Try(() => _store.Update(last.Id, now, last.Label, last.Line, last.Column, request.PaneLayout));
            return new TrailRecordResult(last, false, dayChanged);
        }

        long id = -1;
        Try(() => id = _store.Append(_workspaceKey, now, (int)request.Kind, request.Target, request.Label,
            request.Line, request.Column, request.DisplayMode, request.StagePane, request.PaneLayout));
        var entry = new TrailEntryViewModel(id, request.Kind, request.Target, request.Label, now,
            request.DisplayMode, request.StagePane, request.PaneLayout)
            { Line = request.Line, Column = request.Column };
        Latest = entry;
        return new TrailRecordResult(entry, true, dayChanged);
    }

    public void UpdateLatestFilePosition(string path, int line, int column)
    {
        if (line < 0 || Latest is not { Kind: TrailEntryKind.File } latest
            || !string.Equals(latest.Target, path, StringComparison.OrdinalIgnoreCase)) return;
        latest.Line = line;
        latest.Column = column;
        if (latest.Id >= 0) Try(() => _store.UpdatePosition(latest.Id, line, column));
    }

    public void UpdateLatestPaneLayout(string? paneLayout)
    {
        if (Latest is not { } latest || string.Equals(latest.PaneLayout, paneLayout, StringComparison.Ordinal)) return;
        latest.PaneLayout = paneLayout;
        if (latest.Id >= 0) Try(() => _store.UpdatePaneLayout(latest.Id, paneLayout));
    }

    private static void Try(Action action)
    {
        try { action(); } catch { }
    }
}
