using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services.Search;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>PDF 本文の検索：取り出し（PdfPig）・一時キャッシュ・ページ単位のヒット・順次の流し方。</summary>
public class PdfSearchTests
{
    // ===== 取り出しの整形 =====

    [Theory]
    [InlineData("検 索 結 果", "検索結果")]            // 和文の文字間に挟まった空白は詰める
    [InlineData("hello world", "hello world")]          // 英単語の間の空白は残す
    [InlineData("PDF の 本文", "PDF の本文")]           // 英字と和文の間は残す（和文どうしだけ詰める）
    [InlineData("a\r\nb\rc\fd", "a\nb\ncd")]           // 改行をそろえ、ページ区切りの \f は除く
    public void Normalize_joins_cjk_spacing_only(string input, string expected)
        => Assert.Equal(expected, PdfTextExtractor.Normalize(input));

    // ===== ページ単位のヒット =====

    [Fact]
    public void FindPdfMatches_reports_page_number_as_line()
    {
        var pages = new[] { "intro\nnothing", "second page\nhas the Needle here", "needle again" };
        var hits = WorkspaceSearchService.FindPdfMatches(pages, @"C:\w\a.pdf", "a.pdf", "needle", null,
            StringComparison.OrdinalIgnoreCase, max: 10);

        Assert.Equal(new[] { 2, 3 }, hits.Select(h => h.Line));
        Assert.Equal("has the Needle here", hits[0].LineText);
        Assert.Equal(9, hits[0].Column);
    }

    [Fact]
    public void FindPdfMatches_stops_at_max()
    {
        var pages = Enumerable.Repeat("needle", 5).ToArray();
        var hits = WorkspaceSearchService.FindPdfMatches(pages, "a.pdf", "a.pdf", "needle", null,
            StringComparison.Ordinal, max: 2);
        Assert.Equal(2, hits.Count);
    }

    // ===== キャッシュ =====

    [Fact]
    public void Cache_stores_extracted_text_and_rekeys_when_pdf_changes()
    {
        var dir = NewTempDir();
        var cacheDir = Path.Combine(dir, "cache");
        try
        {
            var pdf = Path.Combine(dir, "doc.pdf");
            WritePdf(pdf, "first page alpha", "second page beta");
            var cache = new PdfTextCache(cacheDir);

            var pages = cache.GetPages(pdf, CancellationToken.None);
            Assert.Equal(2, pages.Count);
            Assert.Contains("alpha", pages[0]);
            Assert.Contains("beta", pages[1]);
            Assert.Single(Directory.GetFiles(cacheDir));

            // 2回目は覚えたものを返す（中身は同じ）
            Assert.Equal(pages, cache.GetPages(pdf, CancellationToken.None));
            Assert.Single(Directory.GetFiles(cacheDir));

            // 書き換えたら取り出し直す（別の鍵になる）
            WritePdf(pdf, "rewritten gamma");
            File.SetLastWriteTimeUtc(pdf, DateTime.UtcNow.AddMinutes(1));
            var after = cache.GetPages(pdf, CancellationToken.None);
            Assert.Single(after);
            Assert.Contains("gamma", after[0]);
            Assert.Equal(2, Directory.GetFiles(cacheDir).Length);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Cache_remembers_unreadable_pdf_as_empty()
    {
        var dir = NewTempDir();
        try
        {
            var broken = Path.Combine(dir, "broken.pdf");
            File.WriteAllText(broken, "this is not a pdf");
            var cache = new PdfTextCache(Path.Combine(dir, "cache"));

            Assert.Empty(cache.GetPages(broken, CancellationToken.None));
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "cache")));
        }
        finally { TryDelete(dir); }
    }

    // ===== サービス結合 =====

    [Fact]
    public async Task GrepPdf_streams_page_hits_for_each_pdf()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            WritePdf(Path.Combine(root, "docs", "a.pdf"), "nothing here", "the needle is on page two");
            WritePdf(Path.Combine(root, "b.pdf"), "needle on first page");
            WritePdf(Path.Combine(root, "c.pdf"), "no match at all");
            File.WriteAllText(Path.Combine(root, "note.txt"), "needle in text");   // テキストは GrepAsync の役目
            var svc = NewService(root);

            var progress = new List<PdfGrepProgress>();
            await foreach (var p in svc.GrepPdfAsync("NEEDLE", new GrepOptions(), CancellationToken.None))
                progress.Add(p);

            Assert.Equal(3, progress.Count);                      // PDF 1本ごとに1回
            Assert.Equal(3, progress[^1].Done);
            Assert.All(progress, p => Assert.Equal(3, p.Total));
            var hits = progress.SelectMany(p => p.Hits).OrderBy(h => h.RelativePath).ToList();
            Assert.Equal(new[] { "b.pdf", "docs/a.pdf" }, hits.Select(h => h.RelativePath));
            Assert.Equal(1, hits[0].Line);
            Assert.Equal(2, hits[1].Line);                        // Line はページ番号
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task GrepPdf_honors_exclude_glob_and_max_results()
    {
        var root = NewTempDir();
        try
        {
            WritePdf(Path.Combine(root, "keep.pdf"), "needle one", "needle two", "needle three");
            WritePdf(Path.Combine(root, "skip.pdf"), "needle skipped");
            var svc = NewService(root);

            var hits = new List<ContentSearchHit>();
            await foreach (var p in svc.GrepPdfAsync("needle",
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
    public void Pdf_match_is_labeled_by_page_and_not_replaceable()
    {
        var pdf = new SearchMatchItem(new ContentSearchHit(@"C:\w\a.pdf", "a.pdf", 3, 1, "needle"));
        var text = new SearchMatchItem(new ContentSearchHit(@"C:\w\a.txt", "a.txt", 3, 1, "needle"));

        Assert.True(pdf.IsPdfPage);
        Assert.Equal("p.3", pdf.LineLabel);
        Assert.False(text.IsPdfPage);
        Assert.Equal("3", text.LineLabel);
    }

    // ===== 補助 =====

    // キャッシュはワークスペースの隠しフォルダーへ（後始末をワークスペースごと消すだけで済ませる）。
    private static WorkspaceSearchService NewService(string root)
    {
        var ws = new FakeWorkspaceService();
        ws.OpenFolder(root);
        return new WorkspaceSearchService(ws, new PdfTextCache(Path.Combine(root, ".pdf-cache")));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LoomoPdfSearchTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WritePdf(string path, params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
            builder.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(25, 700), font);
        File.WriteAllBytes(path, builder.Build());
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 一時フォルダーの掃除失敗は無視 */ }
    }
}
