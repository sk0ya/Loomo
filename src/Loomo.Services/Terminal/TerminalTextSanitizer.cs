using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Services;

/// <summary>端末向け出力から、テキスト表示では意味を持たない ANSI 制御コードを除去する。</summary>
internal static class TerminalTextSanitizer
{
    // CSI（カラー、カーソル等）と OSC（タイトル、ハイパーリンク等）。
    private static readonly Regex AnsiEscapePattern = new(
        @"\x1b(\[[0-9;?]*[ -/]*[@-~]|\].*?(\x07|\x1b\\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string RemoveAnsiEscapes(string text)
        => AnsiEscapePattern.Replace(text, string.Empty);
}
