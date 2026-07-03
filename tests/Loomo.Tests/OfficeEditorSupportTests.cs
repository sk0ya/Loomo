using System;
using System.IO;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Office（Excel/Word）の読み取り専用プレビュー提供者の検証。実ファイル（.xlsx/.docx）を一時生成して
/// HTML へ変換し、セル値・見出し・段落テキストが出力へ載ることを確かめる。どちらもエディタ本文を使わず
/// ファイルパスから読むので、<see cref="IEditorSupportProvider.UsesEditorText"/> が false であることも確認する。
/// </summary>
public class OfficeEditorSupportTests
{
    [Fact]
    public void Excel_各シートのセル値と見出しをHTMLへ出す()
    {
        var path = CreateXlsx();
        try
        {
            var html = new ExcelEditorSupport(new AiSettings()).RenderHtml(path, text: "");

            Assert.StartsWith("<!DOCTYPE html>", html);       // フル HTML 文書
            Assert.Contains("データ", html);                    // シート名（見出し）
            Assert.Contains("名前", html);                      // ヘッダーセル
            Assert.Contains("太郎", html);                      // 文字セル
            Assert.Contains("30", html);                        // 数値セル
            Assert.Contains("office-grid", html);               // テーブルとして描画
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Excel_UsesEditorTextはfalse_本文非依存()
    {
        // 本文（text）に依存しない：空文字を渡してもファイルから読めている。
        Assert.False(new ExcelEditorSupport(new AiSettings()).UsesEditorText);
    }

    [Fact]
    public void Word_段落テキストをHTMLへ変換する()
    {
        var path = CreateDocx("これはテスト段落です。");
        try
        {
            var html = new WordEditorSupport(new AiSettings()).RenderHtml(path, text: "");

            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("これはテスト段落です。", html);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Word_UsesEditorTextはfalse_本文非依存()
    {
        Assert.False(new WordEditorSupport(new AiSettings()).UsesEditorText);
    }

    [Fact]
    public void 壊れたファイルでも例外を投げずエラーページを返す()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        File.WriteAllText(path, "これは Excel ではありません");
        try
        {
            var html = new ExcelEditorSupport(new AiSettings()).RenderHtml(path, text: "");
            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("office-error", html);
        }
        finally { File.Delete(path); }
    }

    private static string CreateXlsx()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("データ");
        ws.Cell(1, 1).Value = "名前";
        ws.Cell(1, 2).Value = "年齢";
        ws.Cell(2, 1).Value = "太郎";
        ws.Cell(2, 2).Value = 30;
        wb.SaveAs(path);
        return path;
    }

    private static string CreateDocx(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".docx");
        using var doc = WordprocessingDocument.Create(
            path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));
        main.Document.Save();
        return path;
    }
}
