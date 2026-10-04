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

    /// <summary>リンクを2つの形で載せる：テキストとしては Markdown、HTML としては &lt;a&gt;。貼る先が
    /// リッチテキスト（Teams・Outlook・Word）ならリンクに、プレーンテキスト（エディタ・チャット）なら
    /// Markdown になる。ロック中の失敗は無視する。</summary>
    public static void SetLink(string markdown, string html)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, markdown);
            data.SetData(DataFormats.Html, HtmlClipboardFormat(html));
            Clipboard.SetDataObject(data, copy: true);
        }
        catch { /* 他アプリがロック中。コピーだけ諦める。 */ }
    }

    /// <summary>Windows の CF_HTML（先頭にバイト位置を書いたヘッダーが要る）。</summary>
    internal static string HtmlClipboardFormat(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\n"
                              + "StartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html><body><!--StartFragment-->";
        const string post = "<!--EndFragment--></body></html>";
        var utf8 = System.Text.Encoding.UTF8;
        var headerLength = utf8.GetByteCount(string.Format(header, 0, 0, 0, 0));
        var startFragment = headerLength + utf8.GetByteCount(pre);
        var endFragment = startFragment + utf8.GetByteCount(fragment);
        var endHtml = endFragment + utf8.GetByteCount(post);
        return string.Format(header, headerLength, endHtml, startFragment, endFragment) + pre + fragment + post;
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
