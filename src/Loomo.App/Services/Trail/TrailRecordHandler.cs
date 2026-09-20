using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

public sealed record TrailRecordRequest(
    TrailEntryKind Kind, string Target, string Label, int Line, int Column,
    DisplayMode DisplayMode, PaneKind? StagePane, string? PaneLayout);

public sealed record TrailRecordResult(TrailEntryViewModel Entry, bool Added, bool LiveDayChanged);

/// <summary>軌跡のデデュープ、永続化、最新地点更新を担当する Command Handler。
/// <para>記録の呼び出し元は UI スレッド（ペイン切替・編集の確定・ファイル移動）なので、SQLite へ書くのは
/// <see cref="TrailStore"/> の書き出しスレッドに任せ、ここでは行への参照だけ受け取って先へ進む。
/// 追記と更新は同じ待ち行列を順に通るため、id が決まる前に更新が走ることはない。§31.15</para></summary>
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
            Try(() => _store.UpdateDeferred(last.Row, now, last.Label, last.Line, last.Column, request.PaneLayout));
            return new TrailRecordResult(last, false, dayChanged);
        }

        TrailRowRef row = TrailRowRef.Resolved(TrailRowRef.Lost);
        Try(() => row = _store.AppendDeferred(_workspaceKey, now, (int)request.Kind, request.Target, request.Label,
            request.Line, request.Column, request.DisplayMode, request.StagePane, request.PaneLayout));
        var entry = new TrailEntryViewModel(row, request.Kind, request.Target, request.Label, now,
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
        Try(() => _store.UpdatePositionDeferred(latest.Row, line, column));
    }

    public void UpdateLatestPaneLayout(string? paneLayout)
    {
        if (Latest is not { } latest || string.Equals(latest.PaneLayout, paneLayout, StringComparison.Ordinal)) return;
        latest.PaneLayout = paneLayout;
        Try(() => _store.UpdatePaneLayoutDeferred(latest.Row, paneLayout));
    }

    private static void Try(Action action)
    {
        try { action(); } catch { }
    }
}
