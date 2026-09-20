using System.Windows;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>デバッグペインの各行（出力・フレーム・変数・ウォッチ・イミディエイト・テスト）を 1 件だけ
/// テキスト化してクリップボードへ送る共通処理。各サブビューの右クリック「コピー」から呼ぶ。</summary>
internal static class DebugItemClipboard
{
    /// <summary>右クリックメニューの配置先要素（その項目の DataContext を引き継ぐ）から 1 件をコピーする。</summary>
    public static void Copy(object? sender)
    {
        var text = (sender as FrameworkElement)?.DataContext switch
        {
            DebugFrameViewModel f => string.IsNullOrEmpty(f.Location) ? f.Name : $"{f.Name}  {f.Location}",
            DebugVariableViewModel v => string.IsNullOrEmpty(v.Value) ? v.Name : $"{v.Name} = {v.Value}",
            WatchItemViewModel w => $"{w.Expression} = {w.Value}",
            ImmediateEntryViewModel im => $"{im.Prompt}\n{im.Result}",
            TestItemViewModel t => string.IsNullOrEmpty(t.Message) ? t.DisplayName : $"{t.DisplayName}  {t.Message}",
            TestGroupViewModel g => $"{g.Name}  {g.CountText}",
            _ => null,
        };
        if (text is not null)
            try { Clipboard.SetText(text); } catch { /* 占有中は無視 */ }
    }
}
