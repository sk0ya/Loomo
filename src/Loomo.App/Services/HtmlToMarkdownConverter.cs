using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Mammoth が生成する意味的 HTML（見出し・段落・箇条書き・表・強調・リンク・画像。すべて自己終了タグの
/// 整形済み XHTML 断片 — <c>&lt;img … /&gt;</c> / <c>&lt;br /&gt;</c> を実機出力で確認済み）を Markdown へ
/// 変換する。<see cref="WordEditorSupport.RenderMarkdown"/>（EditorSupport の「Markdownとして保存」）専用の
/// 軽量変換で、任意の HTML 全般には対応しない。
/// </summary>
internal static class HtmlToMarkdownConverter
{
    // Word の図形／OLE 貼り付けが EMF/WMF（Windows 専用のベクター形式）で埋め込まれることがある。
    // ブラウザにも大半の Markdown ビューアにも描画できないので、data URI として素通ししない。
    private static readonly string[] NonRenderableImageMimeTypes =
        ["image/x-emf", "image/emf", "image/x-wmf", "image/wmf"];

    private static bool IsNonRenderableImageDataUri(string src)
    {
        var match = Regex.Match(src, "^data:([^;,]+)");
        return match.Success
            && NonRenderableImageMimeTypes.Contains(match.Groups[1].Value, StringComparer.OrdinalIgnoreCase);
    }

    public static string Convert(string html)
    {
        XElement root;
        try
        {
            root = XElement.Parse("<root>" + html + "</root>");
        }
        catch (XmlException)
        {
            // 想定外に不正な XML だったときの保険：タグを剥がした素のテキストだけ返す。
            return WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", string.Empty)).Trim();
        }

        var sb = new StringBuilder();
        RenderBlocks(root.Nodes(), sb);
        var text = Regex.Replace(sb.ToString(), "\n{3,}", "\n\n").Trim();
        return text.Length == 0 ? string.Empty : text + "\n";
    }

    private static void RenderBlocks(IEnumerable<XNode> nodes, StringBuilder sb)
    {
        foreach (var node in nodes)
        {
            if (node is XText textNode)
            {
                var text = CollapseWhitespace(textNode.Value);
                if (text.Length > 0)
                    sb.Append(InlineEscape(text)).Append("\n\n");
                continue;
            }
            if (node is not XElement el)
                continue;

            switch (el.Name.LocalName.ToLowerInvariant())
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    var level = el.Name.LocalName[1] - '0';
                    AppendParagraph(sb, new string('#', level) + " " + RenderInline(el).Trim());
                    break;
                case "p":
                    AppendParagraph(sb, EscapeLeadingMarkup(RenderInline(el).Trim()));
                    break;
                case "ul":
                    RenderList(el, sb, ordered: false, depth: 0);
                    sb.Append('\n');
                    break;
                case "ol":
                    RenderList(el, sb, ordered: true, depth: 0);
                    sb.Append('\n');
                    break;
                case "table":
                    RenderTable(el, sb);
                    sb.Append('\n');
                    break;
                case "hr":
                    sb.Append("---\n\n");
                    break;
                default:
                    // blockquote/div 等、Markdown に直接対応しないラッパーは中身をブロックとして展開する。
                    RenderBlocks(el.Nodes(), sb);
                    break;
            }
        }
    }

    private static void AppendParagraph(StringBuilder sb, string text)
    {
        if (text.Length > 0)
            sb.Append(text).Append("\n\n");
    }

    private static void RenderList(XElement listEl, StringBuilder sb, bool ordered, int depth)
    {
        var indent = new string(' ', depth * 2);
        var index = 1;
        foreach (var li in listEl.Elements().Where(e => e.Name.LocalName == "li"))
        {
            var marker = ordered ? $"{index}. " : "- ";
            index++;
            sb.Append(indent).Append(marker).Append(RenderListItemText(li)).Append('\n');

            foreach (var nested in li.Elements().Where(e => e.Name.LocalName is "ul" or "ol"))
                RenderList(nested, sb, nested.Name.LocalName == "ol", depth + 1);
        }
    }

    private static string RenderListItemText(XElement li)
    {
        var sb = new StringBuilder();
        foreach (var node in li.Nodes())
        {
            if (node is XElement e && e.Name.LocalName is "ul" or "ol")
                continue; // ネストしたリストは呼び出し元が別行で展開する。
            AppendInline(node, sb);
        }
        return sb.ToString().Trim();
    }

    private static void RenderTable(XElement table, StringBuilder sb)
    {
        // 直下の行だけを対象にする（Descendants だと、セル内にネストした表の <tr> まで拾って
        // 外側の表の行と混ざってしまう）。
        var rows = DirectTableRows(table).Select(ExpandTableRowCells).Where(r => r.Count > 0).ToList();
        if (rows.Count == 0)
            return;

        // colspan で列数が行ごとにばらつくと Markdown の表として壊れるので、最大列数へ揃える
        // （足りない分は空セルで埋める＝rowspan で欠けた列もこれで一応の体裁は保てる）。
        var columnCount = rows.Max(r => r.Count);

        var first = true;
        foreach (var cells in rows)
        {
            var padded = cells.Count < columnCount
                ? cells.Concat(Enumerable.Repeat(string.Empty, columnCount - cells.Count))
                : cells;
            sb.Append("| ").Append(string.Join(" | ", padded)).Append(" |\n");
            if (first)
            {
                sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", columnCount))).Append(" |\n");
                first = false;
            }
        }
    }

    private static IEnumerable<XElement> DirectTableRows(XElement table)
    {
        foreach (var child in table.Elements())
        {
            if (child.Name.LocalName == "tr")
                yield return child;
            else if (child.Name.LocalName is "tbody" or "thead" or "tfoot")
                foreach (var tr in child.Elements().Where(e => e.Name.LocalName == "tr"))
                    yield return tr;
            // ネストした <table> はここで打ち切り、内側の行を外側の表へ混ぜない。
        }
    }

    private static List<string> ExpandTableRowCells(XElement row)
    {
        var cells = new List<string>();
        foreach (var cell in row.Elements().Where(e => e.Name.LocalName is "td" or "th"))
        {
            var span = int.TryParse((string?)cell.Attribute("colspan"), out var parsed) && parsed > 0 ? parsed : 1;
            cells.Add(RenderTableCellText(cell));
            for (var i = 1; i < span; i++)
                cells.Add(string.Empty);
        }
        return cells;
    }

    private static string RenderTableCellText(XElement cell)
    {
        var text = RenderInline(cell).Replace("\r", "").Replace("\n", " ").Trim();
        return text.Replace("|", "\\|");
    }

    private static string RenderInline(XContainer container)
    {
        var sb = new StringBuilder();
        foreach (var node in container.Nodes())
            AppendInline(node, sb);
        return sb.ToString();
    }

    private static void AppendInline(XNode node, StringBuilder sb)
    {
        if (node is XText textNode)
        {
            sb.Append(InlineEscape(CollapseWhitespace(textNode.Value)));
            return;
        }
        if (node is not XElement el)
            return;

        switch (el.Name.LocalName.ToLowerInvariant())
        {
            case "br":
                sb.Append("  \n");
                break;
            case "strong" or "b":
                WrapInline(sb, el, "**", "**");
                break;
            case "em" or "i":
                WrapInline(sb, el, "*", "*");
                break;
            case "s" or "strike" or "del":
                WrapInline(sb, el, "~~", "~~");
                break;
            case "sup":
                WrapInline(sb, el, "<sup>", "</sup>");
                break;
            case "sub":
                WrapInline(sb, el, "<sub>", "</sub>");
                break;
            case "u":
                WrapInline(sb, el, "<u>", "</u>");
                break;
            case "a":
                var hrefAttr = el.Attribute("href");
                if (hrefAttr is null)
                {
                    // Word の内部ブックマーク（見出しの <a id="…"></a>、href を持たない）はリンクではない。
                    // 中身（通常は空）だけを展開し、無意味な "[]()" を残さない。
                    sb.Append(RenderInline(el));
                    break;
                }
                sb.Append('[').Append(RenderInline(el)).Append("](").Append(hrefAttr.Value).Append(')');
                break;
            case "img":
                var src = (string?)el.Attribute("src") ?? string.Empty;
                var alt = (string?)el.Attribute("alt") ?? string.Empty;
                if (IsNonRenderableImageDataUri(src))
                {
                    // Word の図形／貼り付け画像は EMF/WMF（Windows 専用のベクター形式）のことがあり、
                    // ブラウザや大半の Markdown ビューアでは描画できない。そのまま埋め込むと数百KB～数MBの
                    // 死んだ base64 で .md を膨らませるだけなので、代わりにプレースホルダだけ残す。
                    sb.Append('[').Append(alt.Length > 0 ? "画像: " + InlineEscape(alt) : "画像").Append(']');
                    break;
                }
                sb.Append("![").Append(InlineEscape(alt)).Append("](").Append(src).Append(')');
                break;
            default:
                // p/div 等、インライン文脈（表セル・リスト項目内など）に出てきたブロックタグは
                // 意味を持たないラッパーとして中身だけ展開する。
                var inner = RenderInline(el).Trim();
                if (inner.Length == 0)
                    break;
                if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]))
                    sb.Append(' ');
                sb.Append(inner);
                break;
        }
    }

    private static void WrapInline(StringBuilder sb, XElement el, string open, string close)
    {
        var inner = RenderInline(el);
        var trimmed = inner.Trim();
        if (trimmed.Length == 0)
        {
            sb.Append(inner); // 空白のみ：マーカーで囲まず素通しする。
            return;
        }

        // CommonMark は強調マーカーの内側が空白に接すると閉じない（例："**bold, **" は太字にならない）。
        // 前後の空白をマーカーの外側へ逃がし、隣り合う別の強調と地続きになるのも防ぐ
        // （"<strong>bold, </strong><em>italic</em>" が "**bold, ***italic*" と曖昧に連結するのを回避）。
        var leading = inner[..(inner.Length - inner.TrimStart().Length)];
        var trailing = inner[inner.TrimEnd().Length..];
        sb.Append(leading).Append(open).Append(trimmed).Append(close).Append(trailing);
    }

    private static string CollapseWhitespace(string text) => Regex.Replace(text, @"\s+", " ");

    /// <summary>行頭の文字が Markdown のブロック記法（見出し／箇条書き／引用）と誤認されないようにする。</summary>
    private static string EscapeLeadingMarkup(string text)
    {
        if (text.Length == 0)
            return text;
        if (text[0] is '#' or '-' or '+' or '>')
            return "\\" + text;
        if (char.IsDigit(text[0]) && Regex.IsMatch(text, @"^\d+[.)](\s|$)"))
            return "\\" + text;
        return text;
    }

    private static string InlineEscape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
