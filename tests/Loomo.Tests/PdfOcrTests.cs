using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services.Search;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>スキャンした PDF（本文を持たず画像だけのページ）を Windows の OCR で読む（実機の OCR エンジンを使う結合テスト）。</summary>
public class PdfOcrTests
{
    [Fact]
    public void Scanned_page_is_read_by_windows_ocr()
    {
        var ocr = new WindowsPdfPageOcr();
        if (!ocr.IsAvailable)
            return;   // OCR の言語が入っていない環境では確かめようがない

        var dir = Path.Combine(Path.GetTempPath(), "LoomoOcrTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pdf = Path.Combine(dir, "scan.pdf");
            WriteImageOnlyPdf(pdf, "検索の対象になる文章");

            var lines = PdfTextExtractor.ExtractLines(pdf, ocr, CancellationToken.None);

            var line = Assert.Single(lines);
            Assert.Equal("p.1 (OCR)", line.Location);
            Assert.Contains("検索", line.Text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 一時フォルダーの掃除失敗は無視 */ }
        }
    }

    /// <summary>文字を画像に描いて、その画像だけを貼った PDF を作る（本文のテキストは持たない＝スキャン相当）。</summary>
    private static void WriteImageOnlyPdf(string path, string text)
    {
        byte[] png = Array.Empty<byte>();
        var thread = new Thread(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1200, 300));
                dc.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                    new Typeface("Yu Gothic UI"), 72, Brushes.Black, 1.0), new Point(40, 100));
            }
            var bitmap = new RenderTargetBitmap(1200, 300, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            png = ms.ToArray();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4).AddPng(png, new PdfRectangle(20, 600, 575, 740));
        File.WriteAllBytes(path, builder.Build());
    }
}
