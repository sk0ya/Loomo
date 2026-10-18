using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ファイル検索・テキスト検索を開くとき、エディタやターミナルで選んでいる文字列を最初の検索語にする（§24.2.3）。
/// 選んだ語を探すために開いたのなら打ち直さずに済み、別のものを探すなら全選択のまま打てば置き換わる。
/// 検索語にならない選択（複数行・長すぎる・空白だけ）は使わない——空の欄で開くのと同じに戻す。
/// </summary>
public static partial class PaletteSelectionSeed
{
    /// <summary>これより長い選択は「探す語」ではなく、まとまった本文を選んでいるだけとみなす。</summary>
    public const int MaxChars = 200;

    /// <summary><paramref name="mode"/> で開くときの最初の検索語。使えない選択なら null。</summary>
    public static string? For(PaletteMode mode, string? selection)
    {
        if (mode is not (PaletteMode.File or PaletteMode.Text)) return null;
        if (string.IsNullOrWhiteSpace(selection)) return null;
        var text = selection.Trim();
        if (text.Contains('\n') || text.Contains('\r') || text.Length > MaxChars) return null;
        return mode == PaletteMode.File ? ForFile(text) : text;
    }

    /// <summary>ファイル検索は「ファイル名」か「ワークスペースからの相対パス」に当てるので、
    /// ターミナルやスタックトレースに出る形（<c>C:\…\Foo.cs(12,5)</c>・<c>src/foo.ts:12:5</c>・引用符付き）を寄せる。
    /// 絶対パスはワークスペースの相対パスと照合できないのでファイル名だけにする。</summary>
    private static string? ForFile(string text)
    {
        text = text.Trim('"', '\'', '`');
        text = LocationSuffix().Replace(text, "");
        text = text.Replace('\\', '/');
        if (IsRooted(text))
            text = text[(text.LastIndexOf('/') + 1)..];
        text = text.TrimStart('.', '/');
        return text.Length == 0 ? null : text;
    }

    private static bool IsRooted(string text)
        => text.StartsWith('/') || (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':');

    /// <summary>末尾の行・列（<c>:12</c>・<c>:12:5</c>・<c>(12)</c>・<c>(12,5)</c>）。</summary>
    [GeneratedRegex(@"(:\d+(:\d+)?|\(\d+(,\s*\d+)?\))$")]
    private static partial Regex LocationSuffix();
}
