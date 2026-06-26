using System;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

/// <summary>Markdown プレビューの描画モード。ページ側 JS がこの値で描き分ける。</summary>
public enum PreviewMode
{
    /// <summary>通常のドキュメント表示（縦スクロール）。非 marp 文書は常にこれ。</summary>
    Document,
    /// <summary>フロントマター <c>marp: true</c> を marp-core で描画（既定＝縦並び全表示／発表トグルで1枚ずつ）。</summary>
    Marp,
}

internal static class MarkdownRenderer
{
    /// <summary>
    /// プレビュー内の相対パス画像を解決するための WebView2 仮想ホスト名。
    /// ShellWindow が SetVirtualHostNameToFolderMapping でプレビュー対象ファイルのフォルダへ
    /// マップし、ページには &lt;base href&gt; としてこのホストを埋め込む。
    /// </summary>
    public const string PreviewVirtualHost = "preview.loomo";

    /// <summary>
    /// 同梱 Web アセット（mermaid.min.js 等）を配信する WebView2 仮想ホスト名。
    /// ShellWindow がアプリ出力の Assets/Web フォルダへマップする。
    /// </summary>
    public const string AssetsVirtualHost = "assets.loomo";

    /// <summary>
    /// プレビューページ（フル HTML 文書）自体を配信する WebView2 仮想ホスト名。
    /// <c>NavigateToString</c> は約 2MB が上限で大きな Markdown を取りこぼすため、ShellWindow は
    /// 生成した HTML を一時ファイルへ書き出し、このホスト経由でナビゲートする（サイズ無制限）。
    /// 相対パス画像の <c>&lt;base href&gt;</c>（<see cref="PreviewVirtualHost"/>）とは別オリジン。
    /// </summary>
    public const string PageVirtualHost = "page.loomo";

    public static string RenderToHtml(string markdown, string? title = null, string styleName = "Dracula", string? baseHref = null)
        => MarkdownPage.BuildPage(RenderToBody(markdown), title, styleName, baseHref);

    /// <summary>フロントマターに <c>marp: true</c> があるか（あれば marp-core で忠実描画する）。</summary>
    public static bool IsMarpDocument(string markdown)
    {
        var m = FrontmatterRe.Match(Normalize(markdown));
        if (!m.Success)
            return false;
        foreach (var line in m.Groups[1].Value.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0)
                continue;
            if (line[..idx].Trim().Equals("marp", StringComparison.OrdinalIgnoreCase)
                && line[(idx + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string StripFrontmatter(string normalizedText)
    {
        var m = FrontmatterRe.Match(normalizedText);
        return m.Success ? normalizedText[m.Length..] : normalizedText;
    }

    /// <summary>
    /// ドキュメント本文（&lt;body&gt; の中身）を生成する。フル再ナビゲートを避けてその場更新するとき、
    /// ページ側がこの文字列で <c>document.body.innerHTML</c> を差し替える。
    /// </summary>
    public static string RenderToBody(string markdown)
    {
        var body = new StringBuilder();
        // U+0001 は Inline() のコードスパン退避に使う番兵。原文に紛れ込むと復元が壊れるので除去する。
        ProcessBlocks(StripFrontmatter(Normalize(markdown)).Replace("\u0001", ""), body);
        return body.ToString();
    }

    private static void ProcessBlocks(string text, StringBuilder html)
    {
        var lines = text.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~"))
            {
                var fence = line.TrimStart();
                var fenceChar = fence[0];
                var lang = fence.Length > 3 ? Encode(fence[3..].Trim()) : "";
                // mermaid フェンスは <pre class="mermaid"> として出力し、ページ側の mermaid.js が
                // 図へ変換する（テキストは textContent として読まれるので HTML エンコードでよい）。
                var isMermaid = lang.Equals("mermaid", StringComparison.OrdinalIgnoreCase);
                var cls = lang.Length > 0 ? $" class=\"language-{lang}\"" : "";
                html.Append(isMermaid ? "<pre class=\"mermaid\">" : $"<pre><code{cls}>");
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fenceChar.ToString() + fenceChar + fenceChar))
                {
                    html.Append(Encode(lines[i])).Append('\n');
                    i++;
                }
                i++;
                html.AppendLine(isMermaid ? "</pre>" : "</code></pre>");
                continue;
            }

            var hm = HeaderRe.Match(line);
            if (hm.Success)
            {
                var level = hm.Groups[1].Length;
                var id = hm.Groups[2].Value.Trim().ToLowerInvariant().Replace(' ', '-');
                html.AppendLine($"<h{level} id=\"{Encode(id)}\">{Inline(hm.Groups[2].Value.Trim())}</h{level}>");
                i++;
                continue;
            }

            if (HrRe.IsMatch(line.Trim()) && !ListItemRe.IsMatch(line))
            {
                html.AppendLine("<hr>");
                i++;
                continue;
            }

            if (line.StartsWith(">"))
            {
                var qLines = new List<string>();
                while (i < lines.Length && lines[i].StartsWith(">"))
                {
                    var l = lines[i];
                    qLines.Add(l.Length > 1 && l[1] == ' ' ? l[2..] : l[1..]);
                    i++;
                }
                var inner = new StringBuilder();
                ProcessBlocks(string.Join("\n", qLines), inner);
                html.Append("<blockquote>").Append(inner).AppendLine("</blockquote>");
                continue;
            }

            if (ListItemRe.IsMatch(line))
            {
                RenderList(lines, ref i, html);
                continue;
            }

            if (i + 1 < lines.Length && line.Contains('|')
                && lines[i + 1].Contains('|') && TableSepRe.IsMatch(lines[i + 1]))
            {
                html.AppendLine("<table><thead><tr>");
                foreach (var cell in SplitRow(line))
                    html.AppendLine($"<th>{Inline(cell)}</th>");
                html.AppendLine("</tr></thead><tbody>");
                i += 2;
                while (i < lines.Length && lines[i].Contains('|'))
                {
                    html.AppendLine("<tr>");
                    foreach (var cell in SplitRow(lines[i]))
                        html.AppendLine($"<td>{Inline(cell)}</td>");
                    html.AppendLine("</tr>");
                    i++;
                }
                html.AppendLine("</tbody></table>");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            var paraLines = new List<string>();
            while (i < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[i])
                   && !HeaderRe.IsMatch(lines[i])
                   && !lines[i].TrimStart().StartsWith("```")
                   && !lines[i].TrimStart().StartsWith("~~~")
                   && !lines[i].StartsWith(">")
                   && !ListItemRe.IsMatch(lines[i])
                   && !(HrRe.IsMatch(lines[i].Trim()) && !ListItemRe.IsMatch(lines[i]))
                   && !(lines[i].Contains('|') && i + 1 < lines.Length
                        && lines[i + 1].Contains('|') && TableSepRe.IsMatch(lines[i + 1])))
            {
                paraLines.Add(lines[i]);
                i++;
            }

            if (paraLines.Count > 0)
                html.AppendLine($"<p>{Inline(JoinParagraph(paraLines))}</p>");
        }
    }

    // 段落内の行を連結する。文末がスペース2つ以上で終わる行はハード改行（<br>）にする
    // （CommonMark の hard line break）。それ以外は空白で連結して 1 つの段落にまとめる。
    private static string JoinParagraph(List<string> lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            sb.Append(line.TrimEnd());
            if (i == lines.Count - 1)
                break;
            sb.Append(line.Length - line.TrimEnd(' ').Length >= 2 ? "<br>" : " ");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 連続するリスト項目を、先頭空白の幅でネストさせて描画する。同じインデント幅の項目は同じ
    /// &lt;ul&gt;/&lt;ol&gt; に並び、より深い項目は直前の &lt;li&gt; の中の入れ子リストになる。
    /// 順序付きリストは先頭項目の番号を start 属性に反映する（1 以外始まり／空行分割のルーズリストでも
    /// 番号が 1 に戻らない）。<paramref name="i"/> はこのリスト領域を消費した位置まで進む。
    /// </summary>
    private static void RenderList(string[] lines, ref int i, StringBuilder html)
    {
        var first = ListItemRe.Match(lines[i]);
        var indent = first.Groups[1].Value.Length;
        var ordered = char.IsDigit(first.Groups[2].Value[0]);
        if (ordered)
        {
            var start = first.Groups[2].Value.TrimEnd('.');
            html.AppendLine(start == "1" ? "<ol>" : $"<ol start=\"{start}\">");
        }
        else
        {
            html.AppendLine("<ul>");
        }

        while (i < lines.Length)
        {
            // 空行はルーズリストとして読み飛ばす。次の非空行がリスト項目でなければリスト終了。
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                var j = i;
                while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j])) j++;
                if (j >= lines.Length || !ListItemRe.IsMatch(lines[j])) break;
                i = j;
            }

            var m = ListItemRe.Match(lines[i]);
            if (!m.Success || m.Groups[1].Value.Length < indent)
                break; // リスト外、または親レベルへ戻った → このリストは終了

            html.Append($"<li>{Inline(m.Groups[3].Value)}");
            i++;

            // より深いインデントの後続項目は、この <li> 内の入れ子リストにする。
            while (true)
            {
                var j = i;
                while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j])) j++;
                if (j < lines.Length && ListItemRe.IsMatch(lines[j])
                    && ListItemRe.Match(lines[j]).Groups[1].Value.Length > indent)
                {
                    i = j;
                    RenderList(lines, ref i, html);
                }
                else break;
            }

            html.AppendLine("</li>");
        }

        html.AppendLine(ordered ? "</ol>" : "</ul>");
    }

    private static string Inline(string text)
    {
        var codes = new List<string>();
        text = CodeSpanRe.Replace(text, m =>
        {
            codes.Add($"<code>{Encode(m.Groups[1].Value)}</code>");
            return $"\x01{codes.Count - 1}\x01";
        });

        text = ImageRe.Replace(text, m =>
            $"<img src=\"{EncodeAttribute(SanitizeUrl(m.Groups[2].Value, allowData: true))}\" alt=\"{Encode(m.Groups[1].Value)}\">");
        text = LinkRe.Replace(text, m =>
            $"<a href=\"{EncodeAttribute(SanitizeUrl(m.Groups[2].Value))}\">{Encode(m.Groups[1].Value)}</a>");

        text = BoldStarRe.Replace(text, "<strong>$1</strong>");
        text = BoldUnderRe.Replace(text, "<strong>$1</strong>");
        text = ItalicStarRe.Replace(text, "<em>$1</em>");
        text = ItalicUnderRe.Replace(text, "<em>$1</em>");
        text = StrikeRe.Replace(text, "<del>$1</del>");

        for (var i = 0; i < codes.Count; i++)
            text = text.Replace($"\x01{i}\x01", codes[i]);

        return text;
    }

    private static string[] SplitRow(string line)
    {
        var s = line.Trim();
        if (s.StartsWith("|")) s = s[1..];
        if (s.EndsWith("|")) s = s[..^1];
        return s.Split('|').Select(c => c.Trim()).ToArray();
    }

    internal static string Encode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    internal static string EncodeAttribute(string s) => Encode(s).Replace("'", "&#39;");

    // 危険な URL スキーム（javascript:, vbscript:, data: のスクリプト等）を弾く。プレビューは
    // NavigateToString で読み込まれるため、未検証のリンクをクリックすると任意スクリプトが走り得る。
    // http/https/mailto/tel と、スキームを持たない相対パス・アンカーのみ許可する（画像は data: も可）。
    private static string SanitizeUrl(string url, bool allowData = false)
    {
        var trimmed = url.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon <= 0)
            return trimmed; // 相対パス・アンカー・スキームなし

        // ':' より前に '/' '?' '#' があればスキームではなく相対パス（例: dir/a:b, ?x=:）。
        if (trimmed.AsSpan(0, colon).IndexOfAny('/', '?', '#') >= 0)
            return trimmed;

        var scheme = trimmed[..colon].ToLowerInvariant();
        if (scheme is "http" or "https" or "mailto" or "tel")
            return trimmed;
        if (allowData && scheme == "data")
            return trimmed;

        return "#";
    }

    // 改行正規化＋コードスパン退避の番兵 U+0001 除去（原文に紛れると復元が壊れる）。
    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").Replace(((char)1).ToString(), "");
    // 先頭の YAML フロントマター（--- … --- / …）。先頭限定・閉じは行頭の --- か …。
    private static readonly Regex FrontmatterRe =
        new(@"\A---\n(.*?)\n(?:---|\.\.\.)[ \t]*(?:\n|\z)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HeaderRe = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex HrRe = new(@"^(\-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);
    // 先頭空白(1)・マーカー(2: -/*/+ または "12.")・内容(3)。空白幅でネスト階層を判定する。
    private static readonly Regex ListItemRe = new(@"^([ \t]*)([\-\*\+]|\d+\.)[ \t]+(.+)$", RegexOptions.Compiled);
    private static readonly Regex TableSepRe = new(@"^\|?[\s\-:\|]+\|?$", RegexOptions.Compiled);
    private static readonly Regex CodeSpanRe = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex ImageRe = new(@"!\[([^\]]*)\]\(([^\)]+)\)", RegexOptions.Compiled);
    private static readonly Regex LinkRe = new(@"\[([^\]]+)\]\(([^\)]+)\)", RegexOptions.Compiled);
    private static readonly Regex BoldStarRe = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex BoldUnderRe = new(@"__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex ItalicStarRe = new(@"\*([^\s\*].*?[^\s\*]?)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex ItalicUnderRe = new(@"_([^\s_].*?[^\s_]?)_(?!_)", RegexOptions.Compiled);
    private static readonly Regex StrikeRe = new(@"~~(.+?)~~", RegexOptions.Compiled);

}
