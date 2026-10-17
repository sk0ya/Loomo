using System.Text;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// エディタが<b>保存するときに使う</b>改行・文字コードを選ぶパレットの一覧（純ロジック）。エディタのフッターの
/// 改行（<c>[dos]</c>）・文字コード（<c>[utf-8]</c>）の表示を押したときに出す。値はエディタの名前
/// （<c>VimEditorControl.SetFileFormat</c>／<c>SetFileEncoding</c> に渡すもの）で、選ぶと本文はそのままで未保存になり、
/// 保存したときにその形で書き出される。
/// </summary>
public static class EditorFileFormatPalette
{
    public const string LineEndingCategory = "改行";
    public const string EncodingCategory = "文字コード";

    private static readonly (string Name, string Label, string Detail)[] LineEndings =
    [
        ("unix", "LF", "Unix・macOS・Git の既定。ほとんどのツールがこれで困らない。"),
        ("dos", "CRLF", "Windows の既定。.bat／.cmd や古い Windows のツール向け。"),
        ("mac", "CR", "古い Mac OS（9 以前）の形式。いまはまず使わない。"),
    ];

    private static readonly (string Name, string Label, string Detail)[] Encodings =
    [
        ("utf-8", "UTF-8", "BOM なしの UTF-8。いまの標準的な形。"),
        ("utf-8-bom", "UTF-8（BOM 付き）", "古い Windows のツール向け。Unix 系のツールは BOM を文字として読んでしまうことがある。"),
        ("shift-jis", "Shift_JIS", "Windows の日本語（cp932）。表せない文字（絵文字など）があると保存前に止める。"),
        ("euc-jp", "EUC-JP", "古い Unix の日本語。表せない文字があると保存前に止める。"),
        ("utf-16le", "UTF-16 LE（BOM 付き）", "Windows の一部のツール向け。テキスト系のツールの多くは読めない。"),
        ("utf-16be", "UTF-16 BE（BOM 付き）", "テキスト系のツールの多くは読めない。"),
        ("latin1", "Latin-1", "西欧の 1 バイト文字コード。日本語は表せない。"),
        ("ascii", "ASCII", "英数字と記号だけ。"),
    ];

    /// <summary>改行の選択肢。<paramref name="current"/> はエディタのいまの値（"unix"／"dos"／"mac"）。</summary>
    public static List<PaletteCommand> BuildLineEndings(string current, Func<string, Action> select)
        => Build(LineEndingCategory, LineEndings, current, select);

    /// <summary>文字コードの選択肢。<paramref name="current"/> はエディタのいまの値（"utf-8"・"shift-jis" など）。</summary>
    public static List<PaletteCommand> BuildEncodings(string current, Func<string, Action> select)
        => Build(EncodingCategory, Encodings, current, select);

    /// <summary>表示名（"dos" → "CRLF"、"shift-jis" → "Shift_JIS"）。知らない名前はそのまま。</summary>
    public static string Label(string name)
        => LineEndings.Concat(Encodings).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            .Label ?? name;

    private static List<PaletteCommand> Build(string category, (string Name, string Label, string Detail)[] options,
        string current, Func<string, Action> select)
        => options.Select(option =>
        {
            var isCurrent = string.Equals(option.Name, current, StringComparison.OrdinalIgnoreCase);
            return new PaletteCommand(category, option.Label, isCurrent ? () => { } : select(option.Name))
            {
                Badge = isCurrent ? "いま" : null,
                Detail = option.Detail + Environment.NewLine + (isCurrent
                    ? "いまこの形で保存されます。"
                    : "選ぶと、保存したときにこの形で書き出します（本文は変えず、未保存になります）。"),
            };
        }).ToList();

    /// <summary>
    /// <paramref name="text"/> を <paramref name="encodingName"/>（エディタの名前）で書き出すと失われる最初の文字。
    /// 失われないなら null。エディタの保存はその文字を「?」に置き換えて黙って書くので、選ぶ前にここで止める。
    /// UTF-8・UTF-16 はどの文字も表せる。
    /// </summary>
    public static string? FirstUnrepresentable(string text, string encodingName)
    {
        var encoding = encodingName.ToLowerInvariant() switch
        {
            "shift-jis" or "sjis" => TextFileEncoding.ShiftJis,
            "euc-jp" => Strict(51932),
            "latin1" or "iso-8859-1" => Strict(28591),
            "ascii" => Strict(20127),
            _ => null,
        };
        if (encoding is null)
            return null;
        try
        {
            encoding.GetByteCount(text);
            return null;
        }
        catch (EncoderFallbackException ex)
        {
            return ex.CharUnknownHigh != default
                ? new string([ex.CharUnknownHigh, ex.CharUnknownLow])
                : ex.CharUnknown.ToString();
        }
    }

    private static Encoding Strict(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
