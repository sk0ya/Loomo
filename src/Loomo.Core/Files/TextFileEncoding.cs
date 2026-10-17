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

/// <summary>
/// テキストファイルの文字コードを判定して読み、<b>同じ文字コード・同じ BOM で</b>書き戻す。
/// <para>
/// 判定は BOM（UTF-8 / UTF-16 LE・BE）→ BOM 無し UTF-8（厳格）→ Shift_JIS（cp932・厳格）の順。どれにも当てはまらない、
/// または NUL を含む（バイナリ）なら判定できない（null）——<b>分からないものは書き換えない</b>ための null で、
/// 呼び元は読めない扱いにすること。<c>File.ReadAllText</c>／<c>WriteAllText</c> で読み書きすると Shift_JIS の
/// ファイルは化けた文字（U+FFFD）のまま UTF-8 で保存され、元に戻せなくなる。
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
