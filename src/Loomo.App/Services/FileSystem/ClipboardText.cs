using System.Windows;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// クリップボードのテキスト授受。他プロセスがクリップボードを掴んでいると WPF の
/// <see cref="Clipboard"/> は例外を投げる（一瞬で終わる操作なので珍しくない）ため、
/// 読み書きの失敗はここで吸収する。
/// </summary>
public static class ClipboardText
{
    /// <summary>空でないテキストをクリップボードへ設定する。ロック中の失敗は無視する。</summary>
    public static void Set(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try { Clipboard.SetText(text); }
        catch { /* 他アプリがロック中。コピーだけ諦める。 */ }
    }

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
