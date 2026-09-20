using System.Windows;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// クリップボードのテキスト読み出し。他プロセスがクリップボードを掴んでいると WPF の
/// <see cref="Clipboard"/> は例外を投げる（一瞬で終わる操作なので珍しくない）ため、
/// 読み出しは必ずここを通し、失敗は null として扱う。
/// </summary>
public static class ClipboardText
{
    /// <summary>クリップボードのテキスト。テキストが無い／読めないときは null。</summary>
    public static string? TryGet()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch
        {
            return null;   // 他アプリがロック中。比較は諦めるだけでよい。
        }
    }
}
