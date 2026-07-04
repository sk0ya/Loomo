using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// XML / XAML（.xml / .xaml）を折りたたみ可能なツリーで表示する EditorSupport 提供者。
/// JSON プレビュー（<see cref="JsonTreeRenderer"/> / <see cref="JsonPreviewPage"/>）の体裁・テーマ・
/// 折りたたみ／絞り込み JS をそのまま共有し、要素・属性・テキスト・コメント等を色分けして描く。
/// 表示専用（書き戻しなし）。テーマは Markdown プレビューと同じ <c>Appearance.MarkdownPreviewTheme</c>。
/// </summary>
public sealed class XmlEditorSupport : IEditorSupportIncrementalHtmlProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".xml", ".xaml"];

    public XmlEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"XML: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => JsonPreviewPage.BuildPage(
            XmlTreeRenderer.RenderTree(text), DescribeTitle(filePath), _settings.Appearance.MarkdownPreviewTheme);

    public string RenderBody(string filePath, string text) => XmlTreeRenderer.RenderTree(text);

    // ページの体裁（対象ファイル・テーマ）だけを鍵にする＝同じファイルを同じテーマで編集している間は
    // #json-root の差し替えだけで更新できる（JsonEditorSupport と同じ方針）。
    public string PageContextKey(string filePath, string text)
        => string.Join("\n", filePath, _settings.Appearance.MarkdownPreviewTheme);
}

/// <summary>
/// XML テキストを折りたたみツリーの HTML（#json-root の中身）へ変換する純ロジック。
/// JSON ツリーと同じ CSS クラス（<c>.node</c> / <c>.line opening</c> / <c>.children</c> / <c>.caret</c> /
/// <c>.closing</c> と色分けの <c>.k</c>(タグ/属性名)・<c>.s</c>(値/テキスト)・<c>.p</c>(記号)・<c>.meta</c>(件数)）
/// を使い、<see cref="JsonPreviewPage.BuildPage"/> の折りたたみ／絞り込み JS がそのまま効くようにする。
/// 壊れた XML（編集途中）は例外を投げず、簡潔なエラー本文（原文併記）を返す。巨大ファイルでブラウザを
/// 固めないよう出力ノード数に上限を設ける。
/// </summary>
public static class XmlTreeRenderer
{
    /// <summary>出力するノードの上限。超えたら以降を省略して注記する（巨大 XML で UI を固めない）。</summary>
    private const int MaxNodes = 100_000;

    public static string RenderTree(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "<div class=\"empty\">（空のファイル）</div>";

        XDocument doc;
        try
        {
            doc = XDocument.Parse(text);
        }
        catch (Exception ex) when (ex is XmlException or FormatException)
        {
            return RenderError(text, ex);
        }

        var sb = new StringBuilder();
        var budget = MaxNodes;

        // XML 宣言（<?xml …?>）は XDocument.Nodes() には含まれないので、あれば先頭に別途出す。
        if (doc.Declaration is { } decl)
            sb.Append("<div class=\"line\"><span class=\"meta\">")
              .Append(Encode(decl.ToString())).Append("</span></div>");

        foreach (var node in doc.Nodes())
        {
            if (budget <= 0)
                break;
            WriteNode(sb, node, ref budget);
        }

        if (budget <= 0)
            sb.Append("<div class=\"trunc\">… ノードが多すぎるため以降を省略しました</div>");

        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, XNode node, ref int budget)
    {
        if (budget <= 0)
            return;

        switch (node)
        {
            case XElement el:
                WriteElement(sb, el, ref budget);
                break;
            case XCData cdata: // XCData は XText の派生なので先に判定する
                budget--;
                sb.Append("<div class=\"line\"><span class=\"p\">&lt;![CDATA[</span><span class=\"s\">")
                  .Append(Encode(cdata.Value))
                  .Append("</span><span class=\"p\">]]&gt;</span></div>");
                break;
            case XText t:
                budget--;
                sb.Append("<div class=\"line\"><span class=\"s\">")
                  .Append(Encode(t.Value)).Append("</span></div>");
                break;
            case XComment c:
                budget--;
                sb.Append("<div class=\"line\"><span class=\"meta\">&lt;!-- ")
                  .Append(Encode(c.Value)).Append(" --&gt;</span></div>");
                break;
            case XProcessingInstruction pi:
                budget--;
                sb.Append("<div class=\"line\"><span class=\"p\">&lt;?</span><span class=\"k\">")
                  .Append(Encode(pi.Target)).Append("</span> <span class=\"s\">")
                  .Append(Encode(pi.Data)).Append("</span><span class=\"p\">?&gt;</span></div>");
                break;
            case XDocumentType dt:
                budget--;
                sb.Append("<div class=\"line\"><span class=\"meta\">&lt;!DOCTYPE ")
                  .Append(Encode(dt.Name)).Append("&gt;</span></div>");
                break;
        }
    }

    private static void WriteElement(StringBuilder sb, XElement el, ref int budget)
    {
        if (budget <= 0)
            return;
        budget--;

        var name = QualifiedName(el);

        // 子ノードが無い要素は 1 行の自己終了タグ（<tag attr="v"/>）にする。
        if (!el.Nodes().Any())
        {
            sb.Append("<div class=\"line\">");
            AppendOpenTag(sb, el, name, selfClose: true);
            sb.Append("</div>");
            return;
        }

        var childCount = el.Nodes().Count();
        sb.Append("<div class=\"node\"><div class=\"line opening\"><span class=\"caret\"></span>");
        AppendOpenTag(sb, el, name, selfClose: false);
        sb.Append("<span class=\"meta\"> ").Append(childCount).Append(" 要素</span>")
          .Append("<span class=\"preview\"> … <span class=\"p\">&lt;/</span><span class=\"k\">")
          .Append(Encode(name)).Append("</span><span class=\"p\">&gt;</span></span>")
          .Append("</div><div class=\"children\">");

        foreach (var child in el.Nodes())
        {
            if (budget <= 0)
                break;
            WriteNode(sb, child, ref budget);
        }

        sb.Append("</div><div class=\"line closing\"><span class=\"p\">&lt;/</span><span class=\"k\">")
          .Append(Encode(name)).Append("</span><span class=\"p\">&gt;</span></div></div>");
    }

    /// <summary>開始（または自己終了）タグ 1 行分を出す：<c>&lt;tag attr="v" …&gt;</c>（属性は色分け）。</summary>
    private static void AppendOpenTag(StringBuilder sb, XElement el, string name, bool selfClose)
    {
        sb.Append("<span class=\"p\">&lt;</span><span class=\"k\">").Append(Encode(name)).Append("</span>");

        foreach (var attr in el.Attributes())
        {
            sb.Append(' ')
              .Append("<span class=\"k\">").Append(Encode(AttributeName(el, attr))).Append("</span>")
              .Append("<span class=\"p\">=</span>")
              .Append("<span class=\"s\">\"").Append(Encode(attr.Value)).Append("\"</span>");
        }

        sb.Append("<span class=\"p\">").Append(selfClose ? "/&gt;" : "&gt;").Append("</span>");
    }

    /// <summary>要素の名前を、名前空間プレフィックスが解決できればプレフィックス付き（例: <c>x:Name</c>）で返す。</summary>
    private static string QualifiedName(XElement el)
    {
        var prefix = el.GetPrefixOfNamespace(el.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? el.Name.LocalName : prefix + ":" + el.Name.LocalName;
    }

    /// <summary>属性の名前を返す。名前空間宣言（xmlns / xmlns:x）とプレフィックス付き属性をそのまま表記する。</summary>
    private static string AttributeName(XElement owner, XAttribute attr)
    {
        if (attr.IsNamespaceDeclaration)
            return attr.Name.Namespace == XNamespace.None ? "xmlns" : "xmlns:" + attr.Name.LocalName;

        var prefix = owner.GetPrefixOfNamespace(attr.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? attr.Name.LocalName : prefix + ":" + attr.Name.LocalName;
    }

    private static string RenderError(string text, Exception ex)
    {
        var msg = ex is XmlException { LineNumber: > 0 } xe
            ? $"XML を解析できません（{xe.LineNumber} 行目, 位置 {xe.LinePosition}）"
            : "XML を解析できません";
        return "<div class=\"err\"><div class=\"err-head\">" + Encode(msg) + "</div>"
            + "<pre class=\"raw\">" + Encode(text) + "</pre></div>";
    }

    private static string Encode(string s) => MarkdownRenderer.Encode(s);
}
