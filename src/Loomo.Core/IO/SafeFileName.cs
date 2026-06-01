using System.Linq;

namespace sk0ya.Loomo.Core.IO;

/// <summary>ファイル名のサニタイズ（パストラバーサル防止）。</summary>
public static class SafeFileName
{
    /// <summary>
    /// ファイル名に使える文字（英数字・<c>-</c>・<c>_</c>）だけを残す。
    /// 空になり得るので、呼び出し側でフォールバック名を用意すること。
    /// </summary>
    public static string Sanitize(string value) =>
        string.Concat(value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
}
