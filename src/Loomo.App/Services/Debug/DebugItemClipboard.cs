using System.Windows;

namespace sk0ya.Loomo.App.Services;

/// <summary>デバッグ行の既存テキスト整形結果をクリップボードへ送る。</summary>
internal static class DebugItemClipboard
{
    public static void Copy(object? sender)
    {
        var text = DebugItemTextFormatter.Format((sender as FrameworkElement)?.DataContext);
        if (text is not null)
            try { Clipboard.SetText(text); } catch { /* クリップボード占有中は無視 */ }
    }
}
