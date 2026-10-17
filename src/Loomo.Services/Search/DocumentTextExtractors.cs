using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace sk0ya.Loomo.Services.Search;

/// <summary>PDF のページごとのテキストを取り出す（PdfPig）。本文を持たないページは OCR に回せる。</summary>
public static class PdfTextExtractor
{
    /// <summary>1つの PDF で OCR に回すページ数の上限（スキャンの大冊で検索が終わらなくならないように）。</summary>
    public const int MaxOcrPages = 200;

    /// <summary>
    /// ページごとのテキスト。読めない PDF（壊れている・暗号化）は空。ページ単位の失敗はそのページを空にして続ける。
    /// </summary>
    public static IReadOnlyList<string> ExtractPages(string path, CancellationToken ct)
    {
        try
        {
            using var document = PdfDocument.Open(path, new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
            });
            var pages = new List<string>(document.NumberOfPages);
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                ct.ThrowIfCancellationRequested();
                string text;
                try { text = ContentOrderTextExtractor.GetText(document.GetPage(number)); }
                catch (OperationCanceledException) { throw; }
                catch { text = ""; }
                pages.Add(Normalize(text));
            }
            return pages;
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// 検索用の行。本文を持たないページ（スキャンした画像だけのページ）は <paramref name="ocr"/> があれば読み取り、
    /// 位置に「(OCR)」を付ける——読み取りは誤ることがあるので、本文由来と見分けられるようにする。
    /// </summary>
    public static IReadOnlyList<DocumentLine> ExtractLines(string path, IPdfPageOcr? ocr, CancellationToken ct)
    {
        var pages = ExtractPages(path, ct);
        var lines = new List<DocumentLine>();
        var ocrPages = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            var number = i + 1;
            var text = pages[i];
            var location = "p." + number.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text) && ocr is not null && ocrPages < MaxOcrPages)
            {
                ct.ThrowIfCancellationRequested();
                ocrPages++;
                try { text = Normalize(ocr.Recognize(path, number, ct)); }
                catch (OperationCanceledException) { throw; }
                catch { text = ""; }
                location += " (OCR)";
            }
            foreach (var line in text.Split('\n'))
            {
                var one = DocumentTextCache.OneLine(line);
                if (one.Length > 0)
                    lines.Add(new DocumentLine(number, location, one));
            }
        }
        return lines;
    }

    /// <summary>
    /// 検索しやすい形に整える。PDF は文字を座標で置いているだけなので、取り出すと和文の文字の間に
    /// 空白が挟まることがある（「検 索」）——<b>非 ASCII の文字どうしに挟まれた空白だけ</b>を詰める
    /// （英単語の間の空白は残す）。改行は \n にそろえ、\f は取り除く。OCR の結果も同じ癖があるので通す。
    /// </summary>
    internal static string Normalize(string text)
    {
        if (text.Length == 0)
            return text;
        var source = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\f", "");
        var sb = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == ' ' && sb.Length > 0 && sb[^1] > 0x7F)
            {
                var next = i + 1;
                while (next < source.Length && source[next] == ' ')
                    next++;
                if (next < source.Length && source[next] > 0x7F)
                {
                    i = next - 1;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Word・Excel・PowerPoint（Office Open XML＝XML を詰めた zip）から本文を取り出す。追加のライブラリは使わず、
/// 必要なパートだけを <see cref="XmlReader"/> で流し読みする（巨大なブックでも DOM を組まない）。
/// 名前空間の接頭辞はファイルによって違いうるので、要素は<b>ローカル名</b>で見る。
/// </summary>
public static class OfficeTextExtractor
{
    /// <summary>1つの文書から取り出す行数の上限（巨大なブックで検索が終わらなくならないように）。</summary>
    public const int MaxLines = 200_000;

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
    };

    // ===== Word =====

    /// <summary>Word：本文の段落を1行ずつ。位置は空でない段落の通し番号（¶N）。</summary>
    public static IReadOnlyList<DocumentLine> ExtractWord(string path, CancellationToken ct)
        => Guard(() =>
        {
            using var zip = ZipFile.OpenRead(path);
            var part = zip.GetEntry("word/document.xml");
            if (part is null)
                return Array.Empty<DocumentLine>();
            var lines = new List<DocumentLine>();
            foreach (var paragraph in ReadParagraphs(part, "p", "t", ct))
            {
                var number = lines.Count + 1;
                lines.Add(new DocumentLine(number, "¶" + number.ToString(CultureInfo.InvariantCulture), paragraph));
                if (lines.Count >= MaxLines)
                    break;
            }
            return lines;
        });

    // ===== PowerPoint =====

    /// <summary>PowerPoint：スライドの段落を1行ずつ。位置はスライド番号（表示順）。</summary>
    public static IReadOnlyList<DocumentLine> ExtractPowerPoint(string path, CancellationToken ct)
        => Guard(() =>
        {
            using var zip = ZipFile.OpenRead(path);
            var lines = new List<DocumentLine>();
            var slides = OrderedParts(zip, "ppt/presentation.xml", "ppt/_rels/presentation.xml.rels", "sldId", "ppt/");
            for (var i = 0; i < slides.Count; i++)
            {
                if (zip.GetEntry(slides[i].Path) is not { } slide)
                    continue;
                var number = i + 1;
                var location = "スライド " + number.ToString(CultureInfo.InvariantCulture);
                foreach (var paragraph in ReadParagraphs(slide, "p", "t", ct))
                {
                    lines.Add(new DocumentLine(number, location, paragraph));
                    if (lines.Count >= MaxLines)
                        return lines;
                }
            }
            return lines;
        });

    // ===== Excel =====

    /// <summary>Excel：値のあるセルを1行ずつ。位置はシート番号（表示順）、表記は「シート名!セル」。</summary>
    public static IReadOnlyList<DocumentLine> ExtractExcel(string path, CancellationToken ct)
        => Guard(() =>
        {
            using var zip = ZipFile.OpenRead(path);
            var shared = ReadSharedStrings(zip, ct);
            // グラフシート（chartsheets/）・マクロシートはセルを持たず、プレビュー（ClosedXML の Worksheets）にも
            // 並ばない。数えるとそれより後ろのシートの番号がずれるので、ワークシートだけを数える。
            var sheets = OrderedParts(zip, "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "sheet", "xl/")
                .Where(sheet => sheet.Path.Contains("/worksheets/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var lines = new List<DocumentLine>();
            for (var i = 0; i < sheets.Count; i++)
            {
                if (zip.GetEntry(sheets[i].Path) is not { } sheet)
                    continue;
                foreach (var (cellRef, value) in ReadCells(sheet, shared, ct))
                {
                    lines.Add(new DocumentLine(i + 1, $"{sheets[i].Name}!{cellRef}", value));
                    if (lines.Count >= MaxLines)
                        return lines;
                }
            }
            return lines;
        });

    private static List<string> ReadSharedStrings(ZipArchive zip, CancellationToken ct)
    {
        var strings = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is not { } part)
            return strings;
        using var reader = XmlReader.Create(part.Open(), ReaderSettings);
        var current = new StringBuilder();
        var inItem = false;
        var phoneticDepth = 0;   // ふりがな（rPh）の中の t は本文ではない
        var walker = new XmlWalker(reader);
        while (walker.Next())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "si":
                        ct.ThrowIfCancellationRequested();
                        current.Clear();
                        if (reader.IsEmptyElement) strings.Add("");
                        else inItem = true;
                        break;
                    case "rPh" when !reader.IsEmptyElement:
                        phoneticDepth++;
                        break;
                    case "t" when inItem && phoneticDepth == 0 && !reader.IsEmptyElement:
                        current.Append(walker.ReadText());
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "rPh" && phoneticDepth > 0)
                    phoneticDepth--;
                else if (reader.LocalName == "si" && inItem)
                {
                    strings.Add(current.ToString());
                    inItem = false;
                }
            }
        }
        return strings;
    }

    private static IEnumerable<(string Ref, string Value)> ReadCells(ZipArchiveEntry sheet, List<string> shared,
        CancellationToken ct)
    {
        using var reader = XmlReader.Create(sheet.Open(), ReaderSettings);
        string? cellRef = null;
        string? type = null;
        string? value = null;
        var inline = new StringBuilder();
        var walker = new XmlWalker(reader);
        while (walker.Next())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "c":
                        ct.ThrowIfCancellationRequested();
                        cellRef = reader.IsEmptyElement ? null : reader.GetAttribute("r");
                        type = reader.GetAttribute("t");
                        value = null;
                        inline.Clear();
                        break;
                    case "v" when cellRef is not null && !reader.IsEmptyElement:
                        value = walker.ReadText();
                        break;
                    case "t" when cellRef is not null && type == "inlineStr" && !reader.IsEmptyElement:
                        inline.Append(walker.ReadText());
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "c" && cellRef is not null)
            {
                if (Emit(cellRef, type, value, inline, shared) is { } emitted)
                    yield return emitted;
                cellRef = null;
            }
        }
    }

    private static (string Ref, string Value)? Emit(string cellRef, string? type, string? value, StringBuilder inline,
        List<string> shared)
    {
        var text = type switch
        {
            "s" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                   && index >= 0 && index < shared.Count ? shared[index] : null,
            "inlineStr" => inline.ToString(),
            "b" => value == "1" ? "TRUE" : value == "0" ? "FALSE" : value,
            _ => value,   // 数値・数式の文字列結果（str）・エラー（e）はそのまま
        };
        var one = text is null ? "" : DocumentTextCache.OneLine(text);
        return one.Length == 0 ? null : (cellRef, one);
    }

    // ===== 共通 =====

    /// <summary>
    /// 段落（<paramref name="paragraph"/> 要素）ごとに、中のテキスト（<paramref name="text"/> 要素）をつないで返す。
    /// 空の段落は返さない。Word のタブ（w:tab）は空白、改行（w:br / w:cr）も空白にする。
    /// </summary>
    private static IEnumerable<string> ReadParagraphs(ZipArchiveEntry part, string paragraph, string text,
        CancellationToken ct)
    {
        using var reader = XmlReader.Create(part.Open(), ReaderSettings);
        var current = new StringBuilder();
        var depth = 0;   // 入れ子の段落（表のセル内など）も1段落ずつ数える
        var walker = new XmlWalker(reader);
        while (walker.Next())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == paragraph && !reader.IsEmptyElement)
                {
                    ct.ThrowIfCancellationRequested();
                    if (depth > 0 && DocumentTextCache.OneLine(current.ToString()) is { Length: > 0 } outer)
                        yield return outer;
                    current.Clear();
                    depth++;
                }
                else if (reader.LocalName == text && depth > 0 && !reader.IsEmptyElement)
                    current.Append(walker.ReadText());
                else if (reader.LocalName is "tab" or "br" or "cr" && depth > 0)
                    current.Append(' ');
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == paragraph && depth > 0)
            {
                if (DocumentTextCache.OneLine(current.ToString()) is { Length: > 0 } done)
                    yield return done;
                current.Clear();
                depth--;
            }
        }
    }

    /// <summary>
    /// ノードを1つずつ進める。<see cref="XmlReader.ReadElementContentAsString()"/> は要素の<b>次のノードまで</b>進めるので、
    /// 素直に <c>while (reader.Read())</c> で回すとその次のノード（直後の兄弟要素・親の終了タグ）を読み飛ばす
    /// （ふりがなの rPh を読み飛ばして本文に混ぜていた）。本文を読んだ直後の1回だけは Read せずにその位置を返す。
    /// </summary>
    private sealed class XmlWalker(XmlReader reader)
    {
        private bool _advanced;

        public bool Next()
        {
            if (!_advanced)
                return reader.Read();
            _advanced = false;
            return !reader.EOF;
        }

        public string ReadText()
        {
            var text = reader.ReadElementContentAsString();
            _advanced = true;
            return text;
        }
    }

    /// <summary>
    /// 一覧パート（workbook.xml の sheet／presentation.xml の sldId）を表示順に読み、関係（.rels）で
    /// 実際のパートのパスへ解決する。Excel はシート名も返す。
    /// </summary>
    private static List<(string Name, string Path)> OrderedParts(ZipArchive zip, string listPart, string relsPart,
        string itemElement, string baseFolder)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (zip.GetEntry(relsPart) is { } rels)
        {
            using var reader = XmlReader.Create(rels.Open(), ReaderSettings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                    && reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
                    targets[id] = target.StartsWith('/') ? target[1..] : baseFolder + target;
            }
        }

        var parts = new List<(string Name, string Path)>();
        if (zip.GetEntry(listPart) is not { } list)
            return parts;
        using (var reader = XmlReader.Create(list.Open(), ReaderSettings))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != itemElement)
                    continue;
                // r:id の接頭辞はファイルによって違いうるので、ローカル名 id の属性を探す。
                string? relId = null;
                if (reader.MoveToFirstAttribute())
                {
                    do
                    {
                        if (reader.LocalName == "id" && reader.NamespaceURI.Length > 0)
                            relId = reader.Value;
                    } while (reader.MoveToNextAttribute());
                    reader.MoveToElement();
                }
                if (relId is not null && targets.TryGetValue(relId, out var path))
                    parts.Add((reader.GetAttribute("name") ?? "", path));
            }
        }
        return parts;
    }

    private static IReadOnlyList<DocumentLine> Guard(Func<IReadOnlyList<DocumentLine>> extract)
    {
        try { return extract(); }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DocumentLine>(); }   // 壊れている・暗号化（Office の暗号化は zip ではない）
    }
}
