using System;
using System.IO;
using System.IO.Compression;
using System.Text;
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
            Assert.Contains("表示できませんでした", html);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Excel_ふりがな付きセル_重複runでも読み込めて値を出す()
    {
        // 日本語 Excel のふりがな（rPh）は run が重複することがあり、ClosedXML はそのまま読むと
        // 「Phonetic runs must be in ascending order and can't overlap.」で失敗する。rPh を除去して
        // 読めることを確認する（値そのものは残る）。
        var path = CreateXlsx();
        InjectOverlappingPhonetics(path, cellText: "名前");
        try
        {
            var html = new ExcelEditorSupport(new AiSettings()).RenderHtml(path, text: "");

            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.DoesNotContain("表示できませんでした", html);  // エラーページに落ちていない
            Assert.Contains("office-grid", html);              // テーブルとして描画された
            Assert.Contains("名前", html);                      // セル値は保たれる
        }
        finally { File.Delete(path); }
    }

    /// <summary>指定セル値の共有文字列 <c>&lt;si&gt;</c> へ、範囲が重複する rPh を2つ差し込む。</summary>
    private static void InjectOverlappingPhonetics(string xlsxPath, string cellText)
    {
        using var zip = ZipFile.Open(xlsxPath, ZipArchiveMode.Update);
        var entry = zip.GetEntry("xl/sharedStrings.xml")
            ?? throw new InvalidOperationException("sharedStrings.xml が無い");

        string xml;
        using (var r = new StreamReader(entry.Open()))
            xml = r.ReadToEnd();

        // 対象テキストを含む <si> の閉じタグ直前へ、範囲が重なる rPh を差し込む（0-2 と 1-3）。
        // ClosedXML は名前空間プレフィックス付き（</x:si>）で書くので、それに合わせた prefix で
        // rPh も同じ名前空間へ入れる（無名前空間だと ClosedXML はふりがな run と認識せず再現しない）。
        var textIdx = xml.IndexOf(cellText, StringComparison.Ordinal);
        Assert.True(textIdx >= 0, "対象セルの共有文字列が見つからない");

        var siCloseX = xml.IndexOf("</x:si>", textIdx, StringComparison.Ordinal);
        var siClosePlain = xml.IndexOf("</si>", textIdx, StringComparison.Ordinal);
        string prefix;
        int siClose;
        if (siCloseX >= 0 && (siClosePlain < 0 || siCloseX <= siClosePlain))
            (prefix, siClose) = ("x:", siCloseX);
        else
            (prefix, siClose) = ("", siClosePlain);
        Assert.True(siClose >= 0, "対象セルの <si> 閉じタグが見つからない");

        var rph = $"<{prefix}rPh sb=\"0\" eb=\"2\"><{prefix}t>にほ</{prefix}t></{prefix}rPh>"
                + $"<{prefix}rPh sb=\"1\" eb=\"3\"><{prefix}t>ほんご</{prefix}t></{prefix}rPh>";
        xml = xml.Insert(siClose, rph);

        using var w = entry.Open();
        w.SetLength(0);
        var bytes = Encoding.UTF8.GetBytes(xml);
        w.Write(bytes, 0, bytes.Length);
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
