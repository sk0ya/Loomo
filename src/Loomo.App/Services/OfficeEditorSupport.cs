using System;
using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Office 系の読み取り専用プレビュー（Excel/Word）で共有する体裁。テーマは Markdown/JSON プレビューと
/// そろえ、フル HTML 文書の組み立ては <see cref="MarkdownPage.BuildPage(string, string?, string, string?, PreviewMode, string?, bool)"/>
/// に相乗りする（テーマ別 CSS・スクロールバーが得られる）。本文側だけに効く小さな <c>&lt;style&gt;</c> を
/// 差し込んで、表（Excel）や見出し（Word）の見え方を整える。
/// </summary>
internal static class OfficePreview
{
    /// <summary>本文の先頭へ入れる、Office プレビュー用の追加スタイル（body スコープ）。</summary>
    internal const string BodyStyle = """
        <style>
        .office-sheet { margin: 14px 0 6px; font-size: 1.05em; opacity: .85; }
        .office-sheet:first-of-type { margin-top: 2px; }
        .office-scroll { overflow-x: auto; margin-bottom: 10px; }
        table.office-grid { border-collapse: collapse; font-size: 12px; white-space: pre; }
        table.office-grid td, table.office-grid th {
            border: 1px solid color-mix(in srgb, currentColor 22%, transparent);
            padding: 2px 8px; text-align: left; vertical-align: top;
        }
        table.office-grid th.office-rownum {
            position: sticky; left: 0; text-align: right; opacity: .5;
            background: color-mix(in srgb, currentColor 8%, transparent); font-weight: normal;
        }
        .office-empty, .office-trunc { opacity: .6; font-size: .9em; margin: 4px 0 12px; }
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

/// <summary>
/// Excel ブック（.xlsx / .xlsm）の読み取り専用プレビュー。ClosedXML で各ワークシートの使用範囲を読み、
/// HTML テーブルへ整形して EditorSupport ペインの WebView2 へ表示する。エディタ本文（バイナリの文字列化）
/// は使わず、ファイルパスから直接読む（<see cref="UsesEditorText"/> = false）。表示専用で書き戻しはしない。
/// </summary>
public sealed class ExcelEditorSupport : IEditorSupportHtmlProvider
{
    // 1シートあたりの表示上限。巨大ブックでブラウザを固めないための保険（超過分は切って注記する）。
    private const int MaxRows = 2000;
    private const int MaxCols = 100;

    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".xlsx", ".xlsm"];

    public ExcelEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    // .xlsx は ZIP バイナリ。エディタ本文は使わず、ファイルパスから直接読む。
    public bool UsesEditorText => false;

    public string DescribeTitle(string filePath) => $"Excel: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
    {
        var theme = _settings.Appearance.MarkdownPreviewTheme;
        try
        {
            return MarkdownPage.BuildPage(RenderBody(filePath), DescribeTitle(filePath), theme);
        }
        catch (Exception ex)
        {
            return OfficePreview.ErrorPage(filePath, ex, theme);
        }
    }

    private static string RenderBody(string filePath)
    {
        // ファイルパスを直接渡すとブックが開いている間ファイルをロックし、失敗時はハンドルが
        // 残りうる。プレビューはユーザーのファイルをロックすべきでないので、自分で開いたストリーム
        // 経由で読む（using で確実に閉じ、パース失敗でもロックを残さない）。
        using var stream = File.OpenRead(filePath);
        using var workbook = new XLWorkbook(stream);
        var sb = new StringBuilder();
        sb.Append(OfficePreview.BodyStyle);

        var any = false;
        foreach (var sheet in workbook.Worksheets)
        {
            any = true;
            AppendSheet(sb, sheet);
        }
        if (!any)
            sb.Append("<p class=\"office-empty\">ワークシートがありません。</p>");
        return sb.ToString();
    }

    private static void AppendSheet(StringBuilder sb, IXLWorksheet sheet)
    {
        sb.Append("<h2 class=\"office-sheet\">").Append(MarkdownRenderer.Encode(sheet.Name)).Append("</h2>");

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastRow == 0 || lastCol == 0)
        {
            sb.Append("<p class=\"office-empty\">（空のシート）</p>");
            return;
        }

        var rows = Math.Min(lastRow, MaxRows);
        var cols = Math.Min(lastCol, MaxCols);

        sb.Append("<div class=\"office-scroll\"><table class=\"office-grid\"><tbody>");
        for (var r = 1; r <= rows; r++)
        {
            sb.Append("<tr><th class=\"office-rownum\">").Append(r).Append("</th>");
            for (var c = 1; c <= cols; c++)
            {
                var cell = sheet.Cell(r, c);
                string value;
                try { value = cell.GetFormattedString(CultureInfo.CurrentCulture); }
                catch { value = cell.GetString(); }
                sb.Append("<td>").Append(MarkdownRenderer.Encode(value)).Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");

        if (lastRow > rows || lastCol > cols)
            sb.Append("<p class=\"office-trunc\">大きなシートのため ")
              .Append(rows).Append(" 行 × ").Append(cols)
              .Append(" 列までを表示（全 ").Append(lastRow).Append(" 行 × ")
              .Append(lastCol).Append(" 列）。</p>");
    }
}

/// <summary>
/// Word 文書（.docx）の読み取り専用プレビュー。Mammoth で意味的な HTML（見出し・段落・表・箇条書き・
/// 画像は data URI で自己完結）へ変換し、EditorSupport ペインの WebView2 へ表示する。エディタ本文は
/// 使わず、ファイルパスから直接読む（<see cref="UsesEditorText"/> = false）。表示専用で書き戻しはしない。
/// </summary>
public sealed class WordEditorSupport : IEditorSupportHtmlProvider
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
            // Excel と同様、ファイルをロックしないよう自前ストリーム経由で変換する。
            using var stream = File.OpenRead(filePath);
            var result = new Mammoth.DocumentConverter().ConvertToHtml(stream);
            var body = OfficePreview.BodyStyle + "<div class=\"office-doc\">" + result.Value + "</div>";
            return MarkdownPage.BuildPage(body, DescribeTitle(filePath), theme);
        }
        catch (Exception ex)
        {
            return OfficePreview.ErrorPage(filePath, ex, theme);
        }
    }
}
