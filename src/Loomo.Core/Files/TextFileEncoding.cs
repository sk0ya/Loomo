using System.Text;

namespace sk0ya.Loomo.Core.Files;

/// <summary>読み取ったテキストと、書き戻すときに同じ形へ戻すための文字コード情報。</summary>
/// <param name="Text">本文（BOM は含まない）。</param>
/// <param name="Encoding">本文の文字コード（不正なバイト・表せない文字は例外になる厳格版）。</param>
/// <param name="Preamble">ファイル先頭の BOM（無ければ空）。書き戻すときにそのまま付け直す。</param>
/// <param name="Name">表示名（"UTF-8"・"UTF-8 (BOM)"・"Shift_JIS"・"UTF-16 LE (BOM)" など）。</param>
public sealed record DecodedText(string Text, Encoding Encoding, byte[] Preamble, string Name)
{
    public bool HasBom => Preamble.Length > 0;
}

/// <summary>ファイルの文字コード・改行をそろえる変換（§23.3.4）。</summary>
public enum TextConversion
{
    /// <summary>UTF-8（BOM なし）にする。</summary>
    ToUtf8,
    /// <summary>UTF-8（BOM 付き）にする。</summary>
    ToUtf8WithBom,
    /// <summary>改行を LF にそろえる（文字コードはそのまま）。</summary>
    ToLf,
    /// <summary>改行を CRLF にそろえる（文字コードはそのまま）。</summary>
    ToCrLf,
}

/// <summary>
/// テキストファイルの文字コードを判定して読み、<b>同じ文字コード・同じ BOM で</b>書き戻す。
/// <para>
/// 判定は BOM（UTF-8 / UTF-16 LE・BE）→ BOM 無し UTF-8（厳格）→ Shift_JIS（cp932・厳格）の順。どれにも当てはまらない、
/// または NUL を含む（バイナリ）なら判定できない（null）——<b>分からないものは書き換えない</b>ための null で、
/// 呼び元は読めない扱いにすること。<c>File.ReadAllText</c>／<c>WriteAllText</c> で読み書きすると Shift_JIS の
/// ファイルは化けた文字（U+FFFD）のまま UTF-8 で保存され、元に戻せなくなる。
/// </para>
/// <para>
/// 読んで書き戻すと<b>元のバイト列に戻る</b>ことを前提にしている。cp932 には同じ文字を表す別の符号位置
/// （NEC 選定 IBM 拡張の「髙」EEE0・NEC 13 区の「≒」8790 など）があるが、.NET の cp932 はそれらを厳格な読み方では
/// 読めない（例外）ので、そうしたファイルは判定できない＝書き換えない側に倒れる。厳格に読める符号位置は
/// すべて往復で元に戻ることを確かめてある（全 2 バイト符号を総当たり、2026-10-17）。
/// </para>
/// </summary>
public static class TextFileEncoding
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];

    public static Encoding StrictUtf8 { get; } =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Windows の日本語コードページ（cp932）。不正なバイト・表せない文字は例外。</summary>
    public static Encoding ShiftJis => LazyShiftJis.Value;

    private static readonly Lazy<Encoding> LazyShiftJis = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    });

    private static Encoding StrictUtf16(bool bigEndian)
        => new UnicodeEncoding(bigEndian, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>ファイルを読んで判定する。読めない・判定できないときは null。</summary>
    public static DecodedText? ReadFile(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return null; }
        return Decode(bytes);
    }

    /// <summary>バイト列を判定して読む。判定できない（バイナリ・どの文字コードとしても不正）ときは null。</summary>
    public static DecodedText? Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Utf8Bom))
            return TryDecode(bytes, Utf8Bom.Length, StrictUtf8, Utf8Bom, "UTF-8 (BOM)");
        if (bytes.AsSpan().StartsWith(Utf16LeBom))
            return TryDecode(bytes, 2, StrictUtf16(bigEndian: false), Utf16LeBom, "UTF-16 LE (BOM)");
        if (bytes.AsSpan().StartsWith(Utf16BeBom))
            return TryDecode(bytes, 2, StrictUtf16(bigEndian: true), Utf16BeBom, "UTF-16 BE (BOM)");

        if (Array.IndexOf(bytes, (byte)0) >= 0)
            return null;   // BOM 無しで NUL を含むのはバイナリ（BOM 無し UTF-16 は扱わない）
        return TryDecode(bytes, 0, StrictUtf8, [], "UTF-8")
            ?? TryDecode(bytes, 0, ShiftJis, [], "Shift_JIS");
    }

    private static DecodedText? TryDecode(byte[] bytes, int offset, Encoding encoding, byte[] preamble, string name)
    {
        try { return new DecodedText(encoding.GetString(bytes, offset, bytes.Length - offset), encoding, preamble, name); }
        catch (DecoderFallbackException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// 変換後の書き方（文字コード・BOM）と本文を返す。文字コードの変換は本文を変えず、改行の変換は文字コードを変えない。
    /// 書き込みは <see cref="TryWriteFile"/> に渡す（UTF-8 はどの文字も表せるので、文字コードの変換が失敗することはない）。
    /// </summary>
    public static (DecodedText Format, string Text) Apply(DecodedText decoded, TextConversion conversion)
        => conversion switch
        {
            TextConversion.ToUtf8 => (decoded with { Encoding = StrictUtf8, Preamble = [], Name = "UTF-8" }, decoded.Text),
            TextConversion.ToUtf8WithBom =>
                (decoded with { Encoding = StrictUtf8, Preamble = Utf8Bom, Name = "UTF-8 (BOM)" }, decoded.Text),
            TextConversion.ToLf => (decoded, NormalizeLineEndings(decoded.Text, "\n")),
            TextConversion.ToCrLf => (decoded, NormalizeLineEndings(decoded.Text, "\r\n")),
            _ => throw new ArgumentOutOfRangeException(nameof(conversion)),
        };

    /// <summary>CRLF・LF・CR（単独）をすべて <paramref name="newline"/> にする。</summary>
    public static string NormalizeLineEndings(string text, string newline)
    {
        var sb = new StringBuilder(text.Length + text.Length / 32);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                sb.Append(newline);
            }
            else if (c == '\n')
                sb.Append(newline);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="original"/> と同じ文字コード・BOM で <paramref name="text"/> を書き込む。
    /// その文字コードで表せない文字（Shift_JIS に無い絵文字など）があれば書かずに false。
    /// </summary>
    public static bool TryWriteFile(string path, DecodedText original, string text)
    {
        byte[] body;
        try { body = original.Encoding.GetBytes(text); }
        catch (EncoderFallbackException) { return false; }
        var bytes = new byte[original.Preamble.Length + body.Length];
        original.Preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, original.Preamble.Length);
        File.WriteAllBytes(path, bytes);
        return true;
    }
}
