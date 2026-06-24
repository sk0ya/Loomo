using System;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

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
        => BuildPage(RenderToBody(markdown), title, styleName, baseHref);

    /// <summary>
    /// 本文（&lt;body&gt; の中身）だけを生成する。フル再ナビゲートを避けてプレビューを
    /// その場更新（チカチカ防止）するとき、ページ側がこの文字列で <c>document.body.innerHTML</c>
    /// を差し替える。<see cref="RenderToHtml"/> も内部でこれを使う。
    /// </summary>
    public static string RenderToBody(string markdown)
    {
        var body = new StringBuilder();
        // U+0001 は Inline() のコードスパン退避に使う番兵。原文に紛れ込むと復元が壊れるので除去する。
        ProcessBlocks(markdown.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\u0001", ""), body);
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

    private static string Encode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EncodeAttribute(string s) => Encode(s).Replace("'", "&#39;");

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

    private static string BuildPage(string body, string? title, string styleName, string? baseHref = null)
    {
        var t = title != null ? Encode(title) : "Preview";
        var css = PreviewCss(styleName);
        var baseTag = string.IsNullOrEmpty(baseHref) ? "" : $"<base href=\"{EncodeAttribute(baseHref)}\">";
        var mermaidTheme = NormalizeStyle(styleName) is "Light" or "GitHub" ? "default" : "dark";
        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            {{baseTag}}
            <title>{{t}}</title>
            <style>
            {{css}}
            </style>
            <script>
            (() => {
                let suppressScrollMessage = false;
                let pendingApplyRatio = null;  // host(editor)→preview の最新要求（未適用）
                let applyScheduled = false;
                let reportScheduled = false;
                let resizeScheduled = false;
                let lastRatio = 0;  // 最後に意図したスクロール比率（resize 時の貼り直し基準）

                const mermaidTheme = '{{mermaidTheme}}';
                const mermaidSrc = 'https://{{AssetsVirtualHost}}/mermaid.min.js';
                let mermaidRequested = false;

                function scrollMax() {
                    const doc = document.documentElement;
                    return Math.max(0, doc.scrollHeight - window.innerHeight);
                }

                function scrollRatio() {
                    const max = scrollMax();
                    return max <= 0 ? 0 : window.scrollY / max;
                }

                // 本文に mermaid 図があるときだけ mermaid.min.js を遅延ロードして描画する（図の無い
                // ページはランタイムを読み込まない）。本文差し替え後は data-processed の付かない新しい
                // 図だけが run() で描かれる。読込失敗・構文エラーは原文テキストのまま残る。
                function renderMermaid() {
                    if (!document.querySelector('.mermaid')) return;
                    if (window.mermaid) { try { window.mermaid.run(); } catch (e) {} return; }
                    if (mermaidRequested) return;
                    mermaidRequested = true;
                    const s = document.createElement('script');
                    s.src = mermaidSrc;
                    s.onload = () => {
                        try {
                            window.mermaid.initialize({ startOnLoad: false, theme: mermaidTheme, suppressErrorRendering: true });
                            window.mermaid.run();
                        } catch (e) {}
                    };
                    s.onerror = () => { mermaidRequested = false; };
                    document.head.appendChild(s);
                }

                // フル再ナビゲートせず本文だけ差し替える（編集ごとのページ再読込＝チカチカを防ぐ）。
                // 高さが変わるのでスクロールを最後の比率へ貼り直し、mermaid を描き直す。
                function applyBody(html) {
                    suppressScrollMessage = true;
                    document.body.innerHTML = html;
                    renderMermaid();
                    window.scrollTo(0, scrollMax() * lastRatio);
                    requestAnimationFrame(() => requestAnimationFrame(() => { suppressScrollMessage = false; }));
                }

                // 1 フレームに 1 回だけ scrollTo する。連続スクロールで殺到する要求は最新値へ畳む。
                function applyPending() {
                    applyScheduled = false;
                    if (pendingApplyRatio === null) return;
                    const ratio = Math.min(1, Math.max(0, pendingApplyRatio));
                    pendingApplyRatio = null;
                    lastRatio = ratio;
                    suppressScrollMessage = true;
                    window.scrollTo(0, scrollMax() * ratio);
                    // Re-enable only after the resulting 'scroll' event has been dispatched,
                    // so the echo is suppressed regardless of how slow layout/scroll is.
                    requestAnimationFrame(() => requestAnimationFrame(() => { suppressScrollMessage = false; }));
                }

                window.setMarkdownPreviewScrollRatio = ratio => {
                    pendingApplyRatio = Number(ratio) || 0;
                    if (!applyScheduled) {
                        applyScheduled = true;
                        requestAnimationFrame(applyPending);
                    }
                };

                // host(editor)→preview は ExecuteScript ではなく PostWebMessage で届く（コンパイル不要・往復待ちなし）。
                if (window.chrome?.webview) {
                    window.chrome.webview.addEventListener('message', e => {
                        const d = e.data;
                        if (!d) return;
                        if (d.type === 'setScrollRatio') window.setMarkdownPreviewScrollRatio(d.ratio);
                        else if (d.type === 'setBody') applyBody(d.html);
                    });
                }

                // 初期ページ本文の mermaid を描く（スクリプトは head で走るので body 解析後に呼ぶ）。
                if (document.readyState === 'loading')
                    document.addEventListener('DOMContentLoaded', renderMermaid);
                else
                    renderMermaid();

                // preview→host(editor) も 1 フレーム 1 回へ間引いてメッセージの氾濫を防ぐ。
                window.addEventListener('scroll', () => {
                    if (suppressScrollMessage || reportScheduled || !window.chrome?.webview) return;
                    reportScheduled = true;
                    requestAnimationFrame(() => {
                        reportScheduled = false;
                        if (suppressScrollMessage) return;
                        lastRatio = scrollRatio();
                        window.chrome.webview.postMessage({
                            type: 'markdownPreviewScroll',
                            ratio: lastRatio
                        });
                    });
                }, { passive: true });

                // 画面サイズ・ペイン幅・表示切替で innerHeight/scrollHeight が変わると、絶対 scrollY を
                // 保つブラウザの挙動でエディタとの比率がズレる。resize 中は scroll エコーを止めて
                // （リフロー起因の scroll でエディタが飛ぶのを防ぐ）、最後に意図した比率へ貼り直す。
                window.addEventListener('resize', () => {
                    suppressScrollMessage = true;
                    if (resizeScheduled) return;
                    resizeScheduled = true;
                    requestAnimationFrame(() => {
                        resizeScheduled = false;
                        window.scrollTo(0, scrollMax() * lastRatio);
                        requestAnimationFrame(() => requestAnimationFrame(() => { suppressScrollMessage = false; }));
                    });
                });
            })();
            </script>
            </head>
            <body>{{body}}</body>
            </html>
            """;
    }

    public static string NormalizeStyle(string? styleName) =>
        styleName?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark",
            "light" => "Light",
            "github" => "GitHub",
            _ => "Dracula",
        };

    private static string PreviewCss(string styleName) => NormalizeStyle(styleName) switch
    {
        "Light" => BaseCss("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#0969DA", "#8250DF", "#953800", "#57606A", "#116329"),
        "GitHub" => BaseCss("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#0969DA", "#24292F", "#CF222E", "#57606A", "#0550AE"),
        "Dark" => BaseCss("#1E1E1E", "#D4D4D4", "#252526", "#3C3C3C", "#4FC1FF", "#DCDCAA", "#CE9178", "#9CDCFE", "#B5CEA8"),
        _ => BaseCss("#282A36", "#F8F8F2", "#1E1F29", "#44475A", "#8BE9FD", "#BD93F9", "#FFB86C", "#6272A4", "#50FA7B"),
    };

    private static string BaseCss(
        string bg,
        string fg,
        string panel,
        string border,
        string link,
        string heading,
        string strong,
        string muted,
        string code)
    {
        return $$"""
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body {
                background: {{bg}};
                color: {{fg}};
                font-family: 'Segoe UI', Arial, sans-serif;
                font-size: 14px;
                line-height: 1.7;
                padding: 20px 24px 40px;
            }
            h1, h2, h3, h4, h5, h6 {
                color: {{heading}};
                font-weight: 600;
                margin-top: 20px;
                margin-bottom: 8px;
                padding-bottom: 5px;
                border-bottom: 1px solid {{border}};
            }
            h1 { font-size: 1.8em; }
            h2 { font-size: 1.4em; }
            h3 { font-size: 1.15em; border-bottom: none; }
            h4, h5, h6 { font-size: 1em; border-bottom: none; color: {{fg}}; }
            p { margin: 10px 0; }
            a { color: {{link}}; text-decoration: none; }
            a:hover { text-decoration: underline; }
            strong { color: {{strong}}; font-weight: 600; }
            em { color: {{heading}}; font-style: italic; }
            del { color: {{muted}}; text-decoration: line-through; }
            code {
                background: {{panel}};
                color: {{code}};
                padding: 1px 5px;
                border-radius: 3px;
                font-family: 'Cascadia Code', Consolas, monospace;
                font-size: 0.88em;
            }
            pre {
                background: {{panel}};
                border: 1px solid {{border}};
                border-radius: 6px;
                padding: 14px 16px;
                overflow-x: auto;
                margin: 14px 0;
            }
            pre code {
                background: none;
                padding: 0;
                color: {{fg}};
                font-size: 0.87em;
                line-height: 1.5;
            }
            pre.mermaid {
                background: transparent;
                border: none;
                text-align: center;
            }
            pre.mermaid svg { max-width: 100%; }
            blockquote {
                border-left: 4px solid {{muted}};
                margin: 14px 0;
                padding: 8px 16px;
                color: {{muted}};
                background: {{panel}};
                border-radius: 0 4px 4px 0;
            }
            blockquote p { margin: 4px 0; }
            ul, ol { padding-left: 24px; margin: 8px 0; }
            li { margin-bottom: 4px; }
            table { border-collapse: collapse; width: 100%; margin: 14px 0; }
            th, td { border: 1px solid {{border}}; padding: 7px 12px; text-align: left; }
            th { background: {{panel}}; color: {{fg}}; font-weight: 600; }
            img { max-width: 100%; border-radius: 4px; display: block; margin: 8px 0; }
            hr { border: none; border-top: 1px solid {{border}}; margin: 20px 0; }
            """;
    }
}
