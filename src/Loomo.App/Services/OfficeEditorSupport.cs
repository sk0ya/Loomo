using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Office 系の読み取り専用プレビューで共有する体裁。HTML 提供者（Word）はフル HTML 文書の組み立てを
/// <see cref="MarkdownPage.BuildPage(string, string?, string, string?, PreviewMode, string?, bool)"/> に相乗り
/// させ（テーマ別 CSS・スクロールバーが得られる）、本文側だけに効く小さな <c>&lt;style&gt;</c> を差し込む。
/// </summary>
internal static class OfficePreview
{
    /// <summary>本文の先頭へ入れる、Office プレビュー用の追加スタイル（body スコープ）。</summary>
    internal const string BodyStyle = """
        <style>
        .office-empty { opacity: .6; font-size: .9em; margin: 4px 0 12px; }
        .office-error { color: #F85149; }
        </style>
        """;

    /// <summary>読み込み・変換に失敗したときの、テーマ付きの案内ページ。</summary>
    internal static string ErrorPage(string filePath, Exception ex, string theme)
    {
        var body = BodyStyle +
            "<p class=\"office-error\">このファイルを表示できませんでした：" +
            MarkdownRenderer.Encode(ex.Message) + "</p>" +
            "<p class=\"office-empty\">" + MarkdownRenderer.Encode(Path.GetFileName(filePath)) + "</p>";
        return MarkdownPage.BuildPage(body, Path.GetFileName(filePath), theme);
    }
}

/// <summary>Excel の1ワークシート分のデータ（表示用の文字列セル）。</summary>
public sealed record ExcelSheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Excel ブック（.xlsx / .xlsm）を ClosedXML で読み、各ワークシートの使用範囲を「表示用の文字列セル」の
/// 二次元配列へ落とす純ロジック。UI からは <see cref="ExcelEditorSupport"/> がこれを使ってワークシートごとの
/// グリッド（VGrid）を作る。ファイルはロックしないようメモリへ読み、ふりがな（rPh）を除いてから渡す。
/// </summary>
public static class ExcelSheetReader
{
    // 1シートあたりの取り込み上限。グリッドは仮想化されるが、極端に大きいブックでの取り込みを抑える。
    private const int MaxRows = 20000;
    private const int MaxCols = 500;

    public static IReadOnlyList<ExcelSheet> Read(string filePath)
    {
        using var stream = OpenSanitized(filePath);
        using var workbook = new XLWorkbook(stream);
        return workbook.Worksheets.Select(ReadSheet).ToList();
    }

    private static ExcelSheet ReadSheet(IXLWorksheet sheet)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        var rowN = Math.Min(lastRow, MaxRows);
        var colN = Math.Min(lastCol, MaxCols);

        var rows = new List<IReadOnlyList<string>>(rowN);
        for (var r = 1; r <= rowN; r++)
        {
            var cells = new string[colN];
            for (var c = 1; c <= colN; c++)
            {
                var cell = sheet.Cell(r, c);
                try { cells[c - 1] = cell.GetFormattedString(CultureInfo.CurrentCulture); }
                catch { cells[c - 1] = cell.GetString(); }
            }
            rows.Add(cells);
        }
        return new ExcelSheet(sheet.Name, rows);
    }

    /// <summary>
    /// ファイルをメモリへ読み、ふりがな（<c>rPh</c> 要素）を取り除いた xlsx ストリームを返す。
    /// 日本語 Excel はセルにふりがなを持つことがあり、その run が重複／非昇順だと ClosedXML が
    /// 「Phonetic runs must be in ascending order and can't overlap.」で読み込みに失敗する。ふりがなは
    /// 表示に不要なので、shared strings / インライン文字列（<c>xl/*.xml</c>）から <c>rPh</c> を除去する。
    /// 除去対象が無ければ元のバイト列をそのまま返す。
    /// </summary>
    private static MemoryStream OpenSanitized(string filePath)
    {
        var ms = new MemoryStream();
        using (var fs = File.OpenRead(filePath))
            fs.CopyTo(ms);
        ms.Position = 0;

        try
        {
            using var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true);
            // xl 配下の XML パート（sharedStrings・worksheets 等）で rPh を含むものだけ書き換える。
            var targets = zip.Entries
                .Where(e => e.FullName.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
                            && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var entry in targets)
            {
                string content;
                using (var reader = new StreamReader(entry.Open()))
                    content = reader.ReadToEnd();

                if (!content.Contains("rPh", StringComparison.Ordinal))
                    continue;

                var cleaned = StripPhonetics(content);
                if (cleaned == content)
                    continue;

                // Update モードでは既存内容を切り詰めてから書き直す。
                using var write = entry.Open();
                write.SetLength(0);
                var bytes = Encoding.UTF8.GetBytes(cleaned);
                write.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            // 除去に失敗しても元のバイト列で読み込みを試す（rPh が無い健全なファイルは影響なし）。
        }

        ms.Position = 0;
        return ms;
    }

    // <rPh …>…</rPh>（名前空間プレフィックスの有無を問わず）と自己完結形をまるごと消す。XML 宣言や
    // 名前空間宣言はそのまま残したいので、XDocument での再シリアライズ（宣言の encoding が書き換わる）は
    // 使わず、生成物である OOXML に対して要素単位で切り取る。rPh の中身は <t> のみでネストしないため安全。
    private static readonly Regex PhoneticRe = new(
        @"<(?:\w+:)?rPh\b[^>]*?(?:/>|>.*?</(?:\w+:)?rPh>)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>XML 文字列から、名前空間を問わず <c>rPh</c>（ふりがなの run）要素をすべて取り除く。</summary>
    internal static string StripPhonetics(string xml) => PhoneticRe.Replace(xml, string.Empty);
}

/// <summary>
/// Word 文書（.docx）の読み取り専用プレビュー。Mammoth で意味的な HTML（見出し・段落・表・箇条書き・
/// 画像は data URI で自己完結）へ変換し、EditorSupport ペインの WebView2 へ表示する。エディタ本文は
/// 使わず、ファイルパスから直接読む（<see cref="UsesEditorText"/> = false）。表示専用で書き戻しはしない。
/// <see cref="IEditorSupportMarkdownExportProvider"/> も実装し、同じ Mammoth 変換結果を
/// <see cref="HtmlToMarkdownConverter"/> へ通して「Markdownとして保存」に応じる。
/// </summary>
public sealed class WordEditorSupport : IEditorSupportHtmlProvider, IEditorSupportMarkdownExportProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".docx"];

    public WordEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    // .docx は ZIP バイナリ。エディタ本文は使わず、ファイルパスから直接読む。
    public bool UsesEditorText => false;

    public string DescribeTitle(string filePath) => $"Word: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
    {
        var theme = _settings.Appearance.MarkdownPreviewTheme;
        try
        {
            var body = OfficePreview.BodyStyle + "<div class=\"office-doc\">" + ConvertToHtml(filePath) + "</div>";
            return MarkdownPage.BuildPage(body, DescribeTitle(filePath), theme);
        }
        catch (Exception ex)
        {
            return OfficePreview.ErrorPage(filePath, ex, theme);
        }
    }

    public string RenderMarkdown(string filePath, string text) => HtmlToMarkdownConverter.Convert(ConvertToHtml(filePath));

    private static string ConvertToHtml(string filePath)
    {
        // ファイルをロックしないよう自前ストリーム経由で変換する。
        using var stream = File.OpenRead(filePath);
        return new Mammoth.DocumentConverter().ConvertToHtml(stream).Value;
    }
}
