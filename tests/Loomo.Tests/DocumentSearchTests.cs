using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services.Search;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>文書（PDF・Word・Excel・PowerPoint）の本文の検索：取り出し・一時キャッシュ・位置付きのヒット・順次の流し方。</summary>
public class DocumentSearchTests
{
    // ===== 取り出しの整形 =====

    [Theory]
    [InlineData("検 索 結 果", "検索結果")]            // 和文の文字間に挟まった空白は詰める
    [InlineData("hello world", "hello world")]          // 英単語の間の空白は残す
    [InlineData("PDF の 本文", "PDF の本文")]           // 英字と和文の間は残す（和文どうしだけ詰める）
    [InlineData("a\r\nb\rc\fd", "a\nb\ncd")]           // 改行をそろえ、\f は除く
    public void Normalize_joins_cjk_spacing_only(string input, string expected)
        => Assert.Equal(expected, PdfTextExtractor.Normalize(input));

    // ===== 位置付きのヒット =====

    [Fact]
    public void FindDocumentMatches_reports_position_and_location()
    {
        var lines = new[]
        {
            new DocumentLine(1, "p.1", "intro"),
            new DocumentLine(2, "p.2", "has the Needle here"),
            new DocumentLine(3, "p.3", "needle again"),
        };
        var hits = WorkspaceSearchService.FindDocumentMatches(lines, @"C:\w\a.pdf", "a.pdf", "needle", null,
            StringComparison.OrdinalIgnoreCase, max: 10);

        Assert.Equal(new[] { 2, 3 }, hits.Select(h => h.Line));
        Assert.Equal(new[] { "p.2", "p.3" }, hits.Select(h => h.Location));
        Assert.Equal(9, hits[0].Column);
    }

    [Fact]
    public void FindDocumentMatches_stops_at_max()
    {
        var lines = Enumerable.Range(1, 5).Select(i => new DocumentLine(i, $"p.{i}", "needle")).ToArray();
        Assert.Equal(2, WorkspaceSearchService.FindDocumentMatches(lines, "a.pdf", "a.pdf", "needle", null,
            StringComparison.Ordinal, max: 2).Count);
    }

    // ===== 取り出し（形式ごと） =====

    [Fact]
    public void Pdf_lines_carry_page_numbers()
    {
        var dir = NewTempDir();
        try
        {
            var pdf = Path.Combine(dir, "doc.pdf");
            WritePdf(pdf, "first page alpha", "second page beta");

            var lines = PdfTextExtractor.ExtractLines(pdf, ocr: null, CancellationToken.None);

            Assert.Contains(lines, l => l.Position == 1 && l.Location == "p.1" && l.Text.Contains("alpha"));
            Assert.Contains(lines, l => l.Position == 2 && l.Location == "p.2" && l.Text.Contains("beta"));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Pdf_page_without_text_goes_to_ocr()
    {
        var dir = NewTempDir();
        try
        {
            var pdf = Path.Combine(dir, "scan.pdf");
            WritePdf(pdf, "text page", "");   // 2ページ目は本文なし（スキャン相当）
            var ocr = new FakeOcr("読 み 取 っ た 本文");

            var lines = PdfTextExtractor.ExtractLines(pdf, ocr, CancellationToken.None);

            Assert.Equal(new[] { 2 }, ocr.Pages);                       // 本文のあるページは OCR しない
            var scanned = Assert.Single(lines, l => l.Position == 2);
            Assert.Equal("p.2 (OCR)", scanned.Location);
            Assert.Equal("読み取った本文", scanned.Text);                // OCR の文字間の空白も詰める
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Word_paragraphs_are_lines()
    {
        var dir = NewTempDir();
        try
        {
            var docx = Path.Combine(dir, "spec.docx");
            WriteZip(docx, ("word/document.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
                  <w:p><w:r><w:t>見</w:t><w:t>出し</w:t></w:r></w:p>
                  <w:p/>
                  <w:p><w:r><w:t xml:space="preserve">要件は </w:t></w:r><w:r><w:t>needle</w:t></w:r><w:r><w:tab/><w:t>です</w:t></w:r></w:p>
                  <w:tbl><w:tr><w:tc><w:p><w:r><w:t>表のセル</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                </w:body></w:document>
                """));

            var lines = OfficeTextExtractor.ExtractWord(docx, CancellationToken.None);

            Assert.Equal(new[] { "見出し", "要件は needle です", "表のセル" }, lines.Select(l => l.Text));
            Assert.Equal(new[] { "¶1", "¶2", "¶3" }, lines.Select(l => l.Location));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Excel_cells_carry_sheet_and_reference()
    {
        var dir = NewTempDir();
        try
        {
            var xlsx = Path.Combine(dir, "book.xlsx");
            using (var workbook = new XLWorkbook())
            {
                workbook.AddWorksheet("概要").Cell("A1").Value = "タイトル";
                var data = workbook.AddWorksheet("データ");
                data.Cell("B3").Value = "needle の値";
                data.Cell("C4").Value = 42;
                workbook.SaveAs(xlsx);
            }

            var lines = OfficeTextExtractor.ExtractExcel(xlsx, CancellationToken.None);

            Assert.Contains(lines, l => l.Position == 1 && l.Location == "概要!A1" && l.Text == "タイトル");
            Assert.Contains(lines, l => l.Position == 2 && l.Location == "データ!B3" && l.Text == "needle の値");
            Assert.Contains(lines, l => l.Position == 2 && l.Location == "データ!C4" && l.Text == "42");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Excel_shared_strings_skip_phonetic_runs()
    {
        var dir = NewTempDir();
        try
        {
            var xlsx = Path.Combine(dir, "furigana.xlsx");
            WriteZip(xlsx,
                ("xl/workbook.xml", """
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                              xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <sheets><sheet name="名簿" sheetId="1" r:id="rId1"/></sheets></workbook>
                    """),
                ("xl/_rels/workbook.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>
                    """),
                ("xl/sharedStrings.xml", """
                    <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                      <si><t>山田</t><rPh sb="0" eb="2"><t>ヤマダ</t></rPh></si></sst>
                    """),
                ("xl/worksheets/sheet1.xml", """
                    <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                      <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="inlineStr"><is><t>直書き</t></is></c></row>
                    </sheetData></worksheet>
                    """));

            var lines = OfficeTextExtractor.ExtractExcel(xlsx, CancellationToken.None);

            Assert.Equal(new[] { ("名簿!A1", "山田"), ("名簿!B1", "直書き") }, lines.Select(l => (l.Location, l.Text)));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Excel_chart_sheets_do_not_shift_sheet_numbers()
    {
        var dir = NewTempDir();
        try
        {
            var xlsx = Path.Combine(dir, "chart.xlsx");
            WriteZip(xlsx,
                ("xl/workbook.xml", """
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                              xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <sheets><sheet name="Chart1" sheetId="1" r:id="rId1"/><sheet name="Data" sheetId="2" r:id="rId2"/></sheets></workbook>
                    """),
                ("xl/_rels/workbook.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="chartsheet" Target="chartsheets/sheet1.xml"/>
                      <Relationship Id="rId2" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>
                    """),
                ("xl/chartsheets/sheet1.xml", "<chartsheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>"),
                ("xl/worksheets/sheet1.xml", """
                    <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                      <row r="3"><c r="B3" t="inlineStr"><is><t>needle</t></is></c></row></sheetData></worksheet>
                    """));

            var line = Assert.Single(OfficeTextExtractor.ExtractExcel(xlsx, CancellationToken.None));

            // プレビュー（ClosedXML の Worksheets）はグラフシートを並べないので、Data は1枚目として数える。
            Assert.Equal((1, "Data!B3"), (line.Position, line.Location));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void PowerPoint_slides_follow_presentation_order()
    {
        var dir = NewTempDir();
        try
        {
            var pptx = Path.Combine(dir, "deck.pptx");
            const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            static string Slide(string text) => $"""
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="{a}">
                  <p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>
                """;
            WriteZip(pptx,
                ("ppt/presentation.xml", """
                    <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                                    xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <p:sldIdLst><p:sldId id="256" r:id="rId3"/><p:sldId id="257" r:id="rId2"/></p:sldIdLst></p:presentation>
                    """),
                ("ppt/_rels/presentation.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId2" Type="slide" Target="slides/slide1.xml"/>
                      <Relationship Id="rId3" Type="slide" Target="slides/slide2.xml"/></Relationships>
                    """),
                ("ppt/slides/slide1.xml", Slide("二枚目に出る needle")),
                ("ppt/slides/slide2.xml", Slide("表紙")));

            var lines = OfficeTextExtractor.ExtractPowerPoint(pptx, CancellationToken.None);

            // ファイル名の番号ではなく、presentation.xml の並び（表示順）でスライド番号を振る。
            Assert.Equal(new[] { (1, "スライド 1", "表紙"), (2, "スライド 2", "二枚目に出る needle") },
                lines.Select(l => (l.Position, l.Location, l.Text)));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Broken_documents_yield_nothing()
    {
        var dir = NewTempDir();
        try
        {
            foreach (var name in new[] { "x.pdf", "x.docx", "x.xlsx", "x.pptx" })
                File.WriteAllText(Path.Combine(dir, name), "this is not a document");
            var cache = new DocumentTextCache(Path.Combine(dir, ".cache"));

            foreach (var name in new[] { "x.pdf", "x.docx", "x.xlsx", "x.pptx" })
                Assert.Empty(cache.GetLines(Path.Combine(dir, name), CancellationToken.None));
            Assert.Equal(4, Directory.GetFiles(Path.Combine(dir, ".cache")).Length);   // 「本文なし」も覚える
        }
        finally { TryDelete(dir); }
    }

    // ===== キャッシュ =====

    [Fact]
    public void Cache_stores_extracted_text_and_rekeys_when_document_changes()
    {
        var dir = NewTempDir();
        var cacheDir = Path.Combine(dir, "cache");
        try
        {
            var pdf = Path.Combine(dir, "doc.pdf");
            WritePdf(pdf, "first page alpha", "second page beta");
            var cache = new DocumentTextCache(cacheDir);

            var lines = cache.GetLines(pdf, CancellationToken.None);
            Assert.Contains(lines, l => l.Location == "p.2" && l.Text.Contains("beta"));
            Assert.Single(Directory.GetFiles(cacheDir));

            // 2回目は覚えたものを返す（中身は同じ）
            Assert.Equal(lines, cache.GetLines(pdf, CancellationToken.None));
            Assert.Single(Directory.GetFiles(cacheDir));

            // 書き換えたら取り出し直す（別の鍵になる）
            WritePdf(pdf, "rewritten gamma");
            File.SetLastWriteTimeUtc(pdf, DateTime.UtcNow.AddMinutes(1));
            Assert.Contains(cache.GetLines(pdf, CancellationToken.None), l => l.Text.Contains("gamma"));
            Assert.Equal(2, Directory.GetFiles(cacheDir).Length);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Cache_reextracts_when_ocr_becomes_available()
    {
        var dir = NewTempDir();
        try
        {
            var pdf = Path.Combine(dir, "scan.pdf");
            WritePdf(pdf, "");
            var cacheDir = Path.Combine(dir, "cache");

            Assert.Empty(new DocumentTextCache(cacheDir).GetLines(pdf, CancellationToken.None));
            var withOcr = new DocumentTextCache(cacheDir, new FakeOcr("needle"));
            Assert.Contains(withOcr.GetLines(pdf, CancellationToken.None), l => l.Text == "needle");
        }
        finally { TryDelete(dir); }
    }

    // ===== サービス結合 =====

    [Fact]
    public async Task GrepDocuments_streams_hits_for_each_document()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            WritePdf(Path.Combine(root, "docs", "a.pdf"), "nothing here", "the needle is on page two");
            WritePdf(Path.Combine(root, "b.pdf"), "no match at all");
            using (var workbook = new XLWorkbook())
            {
                workbook.AddWorksheet("S").Cell("D2").Value = "needle in a cell";
                workbook.SaveAs(Path.Combine(root, "c.xlsx"));
            }
            File.WriteAllText(Path.Combine(root, "note.txt"), "needle in text");   // テキストは GrepAsync の役目
            var svc = NewService(root);

            var progress = new List<DocumentGrepProgress>();
            await foreach (var p in svc.GrepDocumentsAsync("NEEDLE", new GrepOptions(), CancellationToken.None))
                progress.Add(p);

            Assert.Equal(3, progress.Count);                      // 文書1本ごとに1回
            Assert.Equal(3, progress[^1].Done);
            Assert.All(progress, p => Assert.Equal(3, p.Total));
            var hits = progress.SelectMany(p => p.Hits).OrderBy(h => h.RelativePath).ToList();
            Assert.Equal(new[] { "c.xlsx", "docs/a.pdf" }, hits.Select(h => h.RelativePath));
            Assert.Equal("S!D2", hits[0].Location);
            Assert.Equal(2, hits[1].Line);                        // PDF の Line はページ番号
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task GrepDocuments_honors_exclude_glob_and_max_results()
    {
        var root = NewTempDir();
        try
        {
            WritePdf(Path.Combine(root, "keep.pdf"), "needle one", "needle two", "needle three");
            WritePdf(Path.Combine(root, "skip.pdf"), "needle skipped");
            var svc = NewService(root);

            var hits = new List<ContentSearchHit>();
            await foreach (var p in svc.GrepDocumentsAsync("needle",
                               new GrepOptions(ExcludeGlob: "skip.pdf", MaxResults: 2), CancellationToken.None))
                hits.AddRange(p.Hits);

            Assert.Equal(2, hits.Count);
            Assert.All(hits, h => Assert.Equal("keep.pdf", h.RelativePath));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AdvancedSearch_finds_text_inside_pdf()
    {
        var root = NewTempDir();
        try
        {
            WritePdf(Path.Combine(root, "spec.pdf"), "cover", "requirement needle");
            var svc = NewService(root);

            var hits = await svc.SearchFilesAsync(
                new AdvancedSearchOptions(ContentQuery: "needle", Kind: SearchFileKind.Pdf), CancellationToken.None);

            var file = Assert.Single(hits);
            var match = Assert.Single(file.ContentMatches);
            Assert.Equal(2, match.Line);
        }
        finally { TryDelete(root); }
    }

    // ===== 結果の表示 =====

    [Fact]
    public void Document_match_is_labeled_by_location_and_not_replaceable()
    {
        var pdf = new SearchMatchItem(new ContentSearchHit(@"C:\w\a.pdf", "a.pdf", 3, 1, "needle", "p.3"));
        var xlsx = new SearchMatchItem(new ContentSearchHit(@"C:\w\b.xlsx", "b.xlsx", 2, 1, "needle", "S!D2"));
        var oldPinnedPdf = SearchMatchItem.ForSnapshot(@"C:\w\a.pdf", "a.pdf", 4, 1, "needle");   // 表記を持たない古い写し
        var text = new SearchMatchItem(new ContentSearchHit(@"C:\w\a.txt", "a.txt", 3, 1, "needle"));

        Assert.True(pdf.IsDocumentHit);
        Assert.Equal("p.3", pdf.LineLabel);
        Assert.Equal("S!D2", xlsx.LineLabel);
        Assert.Equal("p.4", oldPinnedPdf.LineLabel);
        Assert.False(text.IsDocumentHit);
        Assert.Equal("3", text.LineLabel);
    }

    [Theory]
    [InlineData("A1", 0, 0)]
    [InlineData("B3", 2, 1)]
    [InlineData("AA10", 9, 26)]
    public void Excel_cell_reference_parses_to_grid_position(string cell, int row, int column)
        => Assert.Equal((row, column), ExcelVisual.ParseCell(cell));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3B")]
    [InlineData("B0")]
    public void Excel_cell_reference_rejects_garbage(string? cell)
        => Assert.Null(ExcelVisual.ParseCell(cell));

    // ===== 補助 =====

    // キャッシュはワークスペースの隠しフォルダーへ（後始末をワークスペースごと消すだけで済ませる）。
    private static WorkspaceSearchService NewService(string root)
    {
        var ws = new FakeWorkspaceService();
        ws.OpenFolder(root);
        return new WorkspaceSearchService(ws, new DocumentTextCache(Path.Combine(root, ".doc-cache")));
    }

    private sealed class FakeOcr(string text) : IPdfPageOcr
    {
        public List<int> Pages { get; } = new();
        public bool IsAvailable => true;

        public string Recognize(string pdfPath, int pageNumber, CancellationToken ct)
        {
            Pages.Add(pageNumber);
            return text;
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LoomoDocSearchTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WritePdf(string path, params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(PageSize.A4);
            if (text.Length > 0)
                page.AddText(text, 12, new PdfPoint(25, 700), font);
        }
        File.WriteAllBytes(path, builder.Build());
    }

    private static void WriteZip(string path, params (string Name, string Content)[] parts)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 一時フォルダーの掃除失敗は無視 */ }
    }
}
