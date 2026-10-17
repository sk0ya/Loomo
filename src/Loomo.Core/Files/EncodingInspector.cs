using System.Globalization;
using System.Text;

namespace sk0ya.Loomo.Core.Files;

/// <summary>文字コードの調査で見つけたものの種類。</summary>
public enum EncodingFindingKind
{
    /// <summary>見た目では分からない文字（全角スペース・ノーブレークスペース・ゼロ幅文字・制御文字など）。</summary>
    Invisible,
    /// <summary>表示の向きを変える制御文字（見た目と実際の並びが食い違う。Trojan Source）。</summary>
    Bidi,
    /// <summary>文字化けの跡（置換文字 U+FFFD・別の文字コードで読んだときに出る並び）。</summary>
    Mojibake,
}

/// <summary>文字コードの調査で見つけた1件。<paramref name="Line"/>・<paramref name="Column"/> は1始まり（列は UTF-16 単位）。</summary>
public sealed record EncodingFinding(int Line, int Column, EncodingFindingKind Kind, string Description, string LineText);

/// <summary>文字コードの調査結果。</summary>
/// <param name="EncodingName">判定した文字コード（"UTF-8"・"Shift_JIS"・"UTF-8 (BOM)" など）。判定できなければ null。</param>
/// <param name="CrLf">CRLF の数。</param>
/// <param name="Lf">LF だけの改行の数。</param>
/// <param name="Cr">CR だけの改行の数（古い Mac の形式）。</param>
/// <param name="EndsWithNewline">最後が改行で終わっているか。</param>
/// <param name="Findings">見つけたもの（ファイルの先頭から。<see cref="EncodingInspector.MaxFindings"/> 件まで）。</param>
/// <param name="Truncated">見つけたものが上限を超えて、先頭の一部だけか。</param>
public sealed record EncodingReport(
    string? EncodingName,
    bool HasBom,
    int CrLf,
    int Lf,
    int Cr,
    bool EndsWithNewline,
    IReadOnlyList<EncodingFinding> Findings,
    bool Truncated)
{
    /// <summary>判定できたか（バイナリ・どの文字コードとしても不正なら false）。</summary>
    public bool IsDecoded => EncodingName is not null;

    /// <summary>改行の形式の表記（"CRLF"・"LF"・"混在（CRLF 12 / LF 3）"・"改行なし"）。</summary>
    public string LineEndingSummary
    {
        get
        {
            var kinds = new List<string>();
            if (CrLf > 0) kinds.Add($"CRLF {CrLf}");
            if (Lf > 0) kinds.Add($"LF {Lf}");
            if (Cr > 0) kinds.Add($"CR {Cr}");
            return kinds.Count switch
            {
                0 => "改行なし",
                1 => kinds[0][..kinds[0].IndexOf(' ')],
                _ => $"混在（{string.Join(" / ", kinds)}）",
            };
        }
    }

    /// <summary>改行の形式が混ざっているか。</summary>
    public bool HasMixedLineEndings => (CrLf > 0 ? 1 : 0) + (Lf > 0 ? 1 : 0) + (Cr > 0 ? 1 : 0) > 1;
}

/// <summary>
/// テキストファイルの文字コード・BOM・改行・見えない文字・文字化けの跡を調べる（純関数）。
/// 文字コードの判定は <see cref="TextFileEncoding"/> と同じ（＝検索・置換が読む形と食い違わない）。
/// </summary>
public static class EncodingInspector
{
    /// <summary>見つけたものとして返す件数の上限（巨大なファイルで一覧が埋まらないように）。</summary>
    public const int MaxFindings = 500;

    public static EncodingReport Inspect(byte[] bytes)
    {
        if (TextFileEncoding.Decode(bytes) is not { } decoded)
            return new EncodingReport(null, false, 0, 0, 0, false, Array.Empty<EncodingFinding>(), false);

        var text = decoded.Text;
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n')
                lf++;
        }

        var findings = new List<EncodingFinding>();
        var truncated = false;
        var lineNumber = 0;
        foreach (var line in SplitLines(text))
        {
            lineNumber++;
            if (!Scan(line, lineNumber, findings))
            {
                truncated = true;
                break;
            }
        }

        var endsWithNewline = text.Length > 0 && text[^1] is '\n' or '\r';
        return new EncodingReport(decoded.Name, decoded.HasBom, crlf, lf, cr, endsWithNewline, findings, truncated);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
                continue;
            yield return text[start..i];
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        if (start < text.Length)
            yield return text[start..];
    }

    /// <summary>1行を調べて見つけたものを足す。上限に達したら false。</summary>
    private static bool Scan(string line, int lineNumber, List<EncodingFinding> findings)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (Describe(c, i == 0 && lineNumber == 1) is { } found)
            {
                if (!Add(findings, new EncodingFinding(lineNumber, i + 1, found.Kind, found.Description, line)))
                    return false;
            }
        }

        // 文字化けの跡は行ごとに1件（同じ行の中で何十件も並べない）。
        if (MojibakePattern(line) is { } mojibake
            && !Add(findings, new EncodingFinding(lineNumber, mojibake.Column, EncodingFindingKind.Mojibake,
                mojibake.Description, line)))
            return false;
        return true;
    }

    private static bool Add(List<EncodingFinding> findings, EncodingFinding finding)
    {
        if (findings.Count >= MaxFindings)
            return false;
        findings.Add(finding);
        return true;
    }

    private static (EncodingFindingKind Kind, string Description)? Describe(char c, bool atFileStart)
    {
        switch (c)
        {
            case '\u3000': return (EncodingFindingKind.Invisible, "全角スペース");
            case '\u00A0': return (EncodingFindingKind.Invisible, "ノーブレークスペース（U+00A0）");
            case '\u200B': return (EncodingFindingKind.Invisible, "ゼロ幅スペース（U+200B）");
            case '\u200C': return (EncodingFindingKind.Invisible, "ゼロ幅非接合子（U+200C）");
            case '\u2060': return (EncodingFindingKind.Invisible, "単語結合子（U+2060）");
            case '\u00AD': return (EncodingFindingKind.Invisible, "ソフトハイフン（U+00AD）");
            case '\uFEFF' when !atFileStart: return (EncodingFindingKind.Invisible, "途中の BOM（U+FEFF）——ファイルを連結した跡");
            case '\u2028': return (EncodingFindingKind.Invisible, "行区切り文字（U+2028）");
            case '\u2029': return (EncodingFindingKind.Invisible, "段落区切り文字（U+2029）");
            case '\uFFFD': return (EncodingFindingKind.Mojibake, "置換文字（U+FFFD）——読めなかったバイトの跡");
        }
        if (c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\u200E' or '\u200F' or '\u061C')
            return (EncodingFindingKind.Bidi, $"表示の向きを変える制御文字（U+{(int)c:X4}）——見た目と実際の並びが食い違う");
        if (c < 0x20 && c != '\t' || c == '\u007F')
            return (EncodingFindingKind.Invisible, $"制御文字（U+{(int)c:X4}）");
        return null;
    }

    // UTF-8 のバイト列を Shift_JIS として読むと、「縺」「繧」「繝」など特定の漢字が密に並ぶ（ひらがな・カタカナの
    // 先頭バイト E3 81/E3 82/E3 83 がこれらに化ける）。普通の日本語にはまず続けて出ない。
    private const string Utf8AsShiftJisMarks = "縺繧繝蜷譁荳螟髢驕";

    /// <summary>文字化けの跡らしい並びを探す。見つかれば最初の位置（1始まり）と説明。</summary>
    private static (int Column, string Description)? MojibakePattern(string line)
    {
        var marks = 0;
        var first = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (Utf8AsShiftJisMarks.Contains(line[i]))
            {
                marks++;
                if (first < 0) first = i;
            }
        }
        if (marks >= 3)
            return (first + 1, "UTF-8 を Shift_JIS として読んだ形（縺・繧・繝…）");

        // UTF-8 を Latin-1／Windows-1252 として読むと「Ã」「â€」「ã」＋記号が出る（é → Ã©、– → â€"、日本語 → ã...）。
        for (var i = 0; i + 1 < line.Length; i++)
        {
            var (c, next) = (line[i], line[i + 1]);
            if ((c is 'Ã' or 'Â' && next is >= '\u0080' and <= '¿')
                || (c == 'â' && next == '€')
                || (c == 'ã' && next is >= '\u0080' and <= '¿' or '€' or '‚' or 'ƒ' or '„' or '…' or '†' or '‡'))
                return (i + 1, "UTF-8 を Latin-1／Windows-1252 として読んだ形（Ã©・â€・ã…）");
        }
        return null;
    }

    /// <summary>見つけたものの1行表示（"L12:5 全角スペース"）。</summary>
    public static string Label(EncodingFinding finding)
        => string.Create(CultureInfo.InvariantCulture, $"L{finding.Line}:{finding.Column} {finding.Description}");
}
