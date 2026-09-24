using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグ／テスト行の表示モデルを、コピー用の一行テキストへ変換する。</summary>
internal static class DebugItemTextFormatter
{
    public static string? Format(object? item) => item switch
    {
        DebugFrameViewModel f => string.IsNullOrEmpty(f.Location) ? f.Name : $"{f.Name}  {f.Location}",
        DebugVariableViewModel v => string.IsNullOrEmpty(v.Value) ? v.Name : $"{v.Name} = {v.Value}",
        WatchItemViewModel w => $"{w.Expression} = {w.Value}",
        ImmediateEntryViewModel im => $"{im.Prompt}\n{im.Result}",
        TestItemViewModel t => string.IsNullOrEmpty(t.Message) ? t.DisplayName : $"{t.DisplayName}  {t.Message}",
        TestGroupViewModel g => $"{g.Name}  {g.CountText}",
        _ => null,
    };
}
