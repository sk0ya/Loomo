using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>日別の軌跡を読み込み、表示モデルへ変換する Query。</summary>
public sealed class TrailHistoryQuery
{
    private readonly TrailStore _store;

    public TrailHistoryQuery(TrailStore store) => _store = store;

    public IReadOnlyList<TrailEntryViewModel> LoadDay(string workspaceKey, DateOnly day) =>
        _store.LoadDay(workspaceKey, day).Select(record => new TrailEntryViewModel(
            record.Id, (TrailEntryKind)record.Kind, record.Target, record.Label, record.Timestamp,
            record.DisplayMode, record.StagePane, record.PaneLayout)
        {
            Line = record.Line,
            Column = record.Column,
        }).ToList();

    public bool HasAny(string workspaceKey) => _store.HasAny(workspaceKey);
}
