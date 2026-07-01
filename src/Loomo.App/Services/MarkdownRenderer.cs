using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Editor.Core.Syntax;

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

    /// <summary>1回のレンダリングを通して引き継ぐ状態：脚注定義／出現順の採番と、目次（[[toc]]）用の見出し一覧。
    /// <see cref="ProcessBlocks"/>／<see cref="RenderList"/>／<see cref="Inline"/> の再帰呼び出し全体で共有する。</summary>
    private sealed class RenderContext
    {
        public readonly Dictionary<string, string> FootnoteDefinitions = new();
        public readonly Dictionary<string, int> FootnoteNumbers = new();
        public readonly List<string> FootnoteOrder = new();
        public int NextFootnoteNumber;
        public List<(int Level, string Id, string Text)> Headings = new();
    }

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

    /// <summary>
    /// ソース上の1行（例 <c>"- [ ] foo"</c>）が GFM タスクリスト項目なら、チェック状態を反転した行を
    /// 返す。タスク項目でなければ <c>null</c>。プレビューでチェックボックスをクリックしたとき、ホストが
    /// 対応するソース行（<c>data-line</c>）にこれを適用してエディタの内容を書き換える。
    /// </summary>
    public static string? ToggleTaskListLine(string line)
    {
        var m = ListItemRe.Match(line);
        if (!m.Success)
            return null;
        var task = TaskItemRe.Match(m.Groups[3].Value);
        if (!task.Success)
            return null;
        var newMark = task.Groups[1].Value.Equals(" ", StringComparison.Ordinal) ? "x" : " ";
        return $"{m.Groups[1].Value}{m.Groups[2].Value} [{newMark}] {task.Groups[2].Value}";
    }

    // フロントマターを取り除き、取り除いた行数（lineOffset）も返す。タスクリストのチェックボックスは
    // ソースファイル上の行番号（data-line）を埋め込む必要があり、フロントマター分だけ本文の行番号と
    // ずれるため、その差分を呼び出し元（ProcessBlocks）へ伝える。
    private static string StripFrontmatter(string normalizedText, out int lineOffset)
    {
        var m = FrontmatterRe.Match(normalizedText);
        if (!m.Success)
        {
            lineOffset = 0;
            return normalizedText;
        }
        lineOffset = normalizedText.AsSpan(0, m.Length).Count('\n');
        return normalizedText[m.Length..];
    }

    /// <summary>
    /// ドキュメント本文（&lt;body&gt; の中身）を生成する。フル再ナビゲートを避けてその場更新するとき、
    /// ページ側がこの文字列で <c>document.body.innerHTML</c> を差し替える。
    /// </summary>
    public static string RenderToBody(string markdown)
    {
        var body = new StringBuilder();
        // Normalize() が Inline() のコードスパン退避番兵（U+0001）を先に除去済みなので、ここでの再除去は不要。
        var stripped = StripFrontmatter(Normalize(markdown), out var lineOffset);
        var ctx = new RenderContext();
        stripped = ExtractFootnoteDefinitions(stripped, ctx);
        ctx.Headings = CollectHeadings(stripped);
        ProcessBlocks(stripped, body, lineOffset, ctx);
        AppendFootnotesSection(body, ctx);
        return body.ToString();
    }

    // 脚注定義（[^id]: 説明、続く行は4スペース/タブ字下げで継続）を本文フローから取り除き、
    // id→本文（Markdown 生テキスト）の対応表を作る。行を削除せず空行に潰すことで、タスクリストの
    // data-line 等が使う行番号（元ファイルの行位置）はズレない。
    private static string ExtractFootnoteDefinitions(string text, RenderContext ctx)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = FootnoteDefRe.Match(lines[i]);
            if (!m.Success)
                continue;
            var id = m.Groups[1].Value;
            var content = new StringBuilder(m.Groups[2].Value.TrimStart());
            lines[i] = "";
            var j = i + 1;
            while (j < lines.Length && lines[j].Length > 0 && (lines[j].StartsWith("    ") || lines[j].StartsWith("\t")))
            {
                content.Append(' ').Append(lines[j].TrimStart());
                lines[j] = "";
                j++;
            }
            ctx.FootnoteDefinitions[id] = content.ToString();
            i = j - 1;
        }
        return string.Join('\n', lines);
    }

    // フェンスの中身は見出しとして数えないよう、フェンス開閉だけを追跡する簡易スキャン。
    private static List<(int Level, string Id, string Text)> CollectHeadings(string text)
    {
        var result = new List<(int, string, string)>();
        var inFence = false;
        var fenceChar = '`';
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                if (!inFence) { inFence = true; fenceChar = trimmed[0]; }
                else if (trimmed[0] == fenceChar) inFence = false;
                continue;
            }
            if (inFence)
                continue;
            var m = HeaderRe.Match(line);
            if (!m.Success)
                continue;
            var headingText = m.Groups[2].Value.Trim();
            result.Add((m.Groups[1].Length, HeadingId(headingText), headingText));
        }
        return result;
    }

    private static string HeadingId(string text) => Encode(text.ToLowerInvariant().Replace(' ', '-'));

    // [[toc]] / [toc] マーカーの位置に埋め込む目次。見出しの入れ子は厳密な <ul><li> ではなく
    // レベル差分の字下げ（既存のテーブル列揃えと同じ、インラインスタイルでの簡易表現）。
    private static string BuildTocHtml(List<(int Level, string Id, string Text)> headings)
    {
        if (headings.Count == 0)
            return "";
        var minLevel = headings.Min(h => h.Level);
        var sb = new StringBuilder("<nav class=\"toc\"><ul>");
        foreach (var h in headings)
            sb.Append($"<li class=\"toc-h{h.Level}\" style=\"padding-left:{(h.Level - minLevel) * 16}px\">")
              .Append($"<a href=\"#{h.Id}\">{Encode(h.Text)}</a></li>");
        sb.Append("</ul></nav>");
        return sb.ToString();
    }

    // 脚注の参照が1つ以上あれば、本文末尾に GFM 風の脚注一覧を追加する（出現順＝採番順）。
    private static void AppendFootnotesSection(StringBuilder html, RenderContext ctx)
    {
        if (ctx.FootnoteOrder.Count == 0)
            return;
        html.AppendLine("<section class=\"footnotes\"><hr><ol>");
        foreach (var id in ctx.FootnoteOrder)
        {
            var num = ctx.FootnoteNumbers[id];
            html.AppendLine(
                $"<li id=\"fn-{num}\">{Inline(ctx.FootnoteDefinitions[id], ctx)} " +
                $"<a href=\"#fnref-{num}\" class=\"footnote-backref\" aria-label=\"本文へ戻る\">↩</a></li>");
        }
        html.AppendLine("</ol></section>");
    }

    private static void ProcessBlocks(string text, StringBuilder html, int lineOffset, RenderContext ctx)
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
                var rawLang = fence.Length > 3 ? fence[3..].Trim() : "";
                // mermaid フェンスは <pre class="mermaid"> として出力し、ページ側の mermaid.js が
                // 図へ変換する（テキストは textContent として読まれるので HTML エンコードでよい）。
                var isMermaid = rawLang.Equals("mermaid", StringComparison.OrdinalIgnoreCase);
                var isDiff = rawLang.Equals("diff", StringComparison.OrdinalIgnoreCase) || rawLang.Equals("patch", StringComparison.OrdinalIgnoreCase);
                var cls = rawLang.Length > 0 ? $" class=\"language-{Encode(rawLang)}\"" : "";
                // コピー用ボタンは pre をラップするコンテナに乗せる（mermaid 図は対象外）。
                if (!isMermaid)
                    html.Append("<div class=\"code-block\"><button type=\"button\" class=\"code-copy-btn\" aria-label=\"コードをコピー\">⧉</button>");
                html.Append(isMermaid ? "<pre class=\"mermaid\">" : $"<pre><code{cls}>");
                i++;
                var codeLines = new List<string>();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fenceChar.ToString() + fenceChar + fenceChar))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                i++;
                if (isMermaid)
                    foreach (var codeLine in codeLines)
                        html.Append(Encode(codeLine)).Append('\n');
                else if (isDiff)
                    AppendDiffHighlightedCode(html, codeLines);
                else
                    AppendHighlightedCode(html, rawLang, codeLines);
                html.Append(isMermaid ? "</pre>" : "</code></pre>");
                html.AppendLine(isMermaid ? "" : "</div>");
                continue;
            }

            if (line.Trim().Equals("[[toc]]", StringComparison.OrdinalIgnoreCase)
                || line.Trim().Equals("[toc]", StringComparison.OrdinalIgnoreCase))
            {
                html.AppendLine(BuildTocHtml(ctx.Headings));
                i++;
                continue;
            }

            var hm = HeaderRe.Match(line);
            if (hm.Success)
            {
                var level = hm.Groups[1].Length;
                var id = HeadingId(hm.Groups[2].Value.Trim());
                html.AppendLine(
                    $"<h{level} id=\"{id}\">" +
                    $"<a class=\"heading-anchor\" href=\"#{id}\" aria-label=\"見出しへのリンクをコピー\">#</a>" +
                    $"{Inline(hm.Groups[2].Value.Trim(), ctx)}</h{level}>");
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
                var quoteStartLine = i;
                var qLines = new List<string>();
                while (i < lines.Length && lines[i].StartsWith(">"))
                {
                    var l = lines[i];
                    qLines.Add(l.Length > 1 && l[1] == ' ' ? l[2..] : l[1..]);
                    i++;
                }
                var inner = new StringBuilder();
                ProcessBlocks(string.Join("\n", qLines), inner, lineOffset + quoteStartLine, ctx);
                html.Append("<blockquote>").Append(inner).AppendLine("</blockquote>");
                continue;
            }

            if (ListItemRe.IsMatch(line))
            {
                RenderList(lines, ref i, html, lineOffset, ctx);
                continue;
            }

            if (i + 1 < lines.Length && line.Contains('|')
                && lines[i + 1].Contains('|') && TableSepRe.IsMatch(lines[i + 1]))
            {
                var aligns = SplitRow(lines[i + 1]).Select(ColumnAlign).ToArray();
                string AlignAttr(int col) =>
                    col < aligns.Length && aligns[col] is { } a ? $" style=\"text-align:{a}\"" : "";

                html.AppendLine("<table><thead><tr>");
                var headerCells = SplitRow(line);
                for (var col = 0; col < headerCells.Length; col++)
                    html.AppendLine($"<th{AlignAttr(col)}>{Inline(headerCells[col], ctx)}</th>");
                html.AppendLine("</tr></thead><tbody>");
                i += 2;
                while (i < lines.Length && lines[i].Contains('|'))
                {
                    html.AppendLine("<tr>");
                    var cells = SplitRow(lines[i]);
                    for (var col = 0; col < cells.Length; col++)
                        html.AppendLine($"<td{AlignAttr(col)}>{Inline(cells[col], ctx)}</td>");
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
                html.AppendLine($"<p>{Inline(JoinParagraph(paraLines), ctx)}</p>");
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
    private static void RenderList(string[] lines, ref int i, StringBuilder html, int lineOffset, RenderContext ctx)
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

            var content = m.Groups[3].Value;
            var task = TaskItemRe.Match(content);
            if (task.Success)
            {
                var isChecked = !task.Groups[1].Value.Equals(" ", StringComparison.Ordinal);
                var checkedAttr = isChecked ? " checked" : "";
                // disabled は付けない：プレビュー上でクリックしてチェック状態を切り替えられる
                // （クリックは JS 側からホストへ postMessage され、ソースの行を書き換える）。
                html.Append(
                    $"<li class=\"task-list-item\"><input type=\"checkbox\" data-line=\"{lineOffset + i}\"{checkedAttr}>" +
                    $"{Inline(task.Groups[2].Value, ctx)}");
            }
            else
            {
                html.Append($"<li>{Inline(content, ctx)}");
            }
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
                    RenderList(lines, ref i, html, lineOffset, ctx);
                }
                else break;
            }

            html.AppendLine("</li>");
        }

        html.AppendLine(ordered ? "</ol>" : "</ul>");
    }

    private static string Inline(string text, RenderContext ctx)
    {
        // コードスパン・画像・リンクの生成済み HTML は番兵に退避してから強調を処理する。
        // そうしないと href やリンクテキスト内の _ * ~ が <em>/<strong>/<del> に化ける
        // （例: [aa_01_cc.md](aa_01_cc.md) の _01_ が斜体になる）。最後に番兵を復元する。
        var spans = new List<string>();
        string Stash(string htmlFragment)
        {
            spans.Add(htmlFragment);
            return $"\x01{spans.Count - 1}\x01";
        }

        text = CodeSpanRe.Replace(text, m => Stash($"<code>{Encode(m.Groups[1].Value)}</code>"));
        text = ImageRe.Replace(text, m =>
            Stash($"<img src=\"{EncodeAttribute(SanitizeUrl(m.Groups[2].Value, allowData: true))}\" alt=\"{Encode(m.Groups[1].Value)}\">"));
        text = LinkRe.Replace(text, m =>
            Stash($"<a href=\"{EncodeAttribute(SanitizeUrl(m.Groups[2].Value))}\">{Encode(m.Groups[1].Value)}</a>"));
        // 脚注参照 [^id]：未定義の id はそのまま地の文として残す（GFM の緩い挙動に合わせる）。
        text = FootnoteRefRe.Replace(text, m =>
        {
            var id = m.Groups[1].Value;
            if (!ctx.FootnoteDefinitions.ContainsKey(id))
                return m.Value;
            if (!ctx.FootnoteNumbers.TryGetValue(id, out var num))
            {
                num = ++ctx.NextFootnoteNumber;
                ctx.FootnoteNumbers[id] = num;
                ctx.FootnoteOrder.Add(id);
            }
            return Stash($"<sup id=\"fnref-{num}\"><a href=\"#fn-{num}\" class=\"footnote-ref\">{num}</a></sup>");
        });
        text = AutolinkRe.Replace(text, m => Stash(AutolinkHtml(m.Value)));

        text = BoldStarRe.Replace(text, "<strong>$1</strong>");
        text = BoldUnderRe.Replace(text, "<strong>$1</strong>");
        text = ItalicStarRe.Replace(text, "<em>$1</em>");
        text = ItalicUnderRe.Replace(text, "<em>$1</em>");
        text = StrikeRe.Replace(text, "<del>$1</del>");

        // 逆順で復元する：コードスパンはリンク／画像より先にスタッシュされるため、リンクテキストに
        // コードスパンを含む場合（例: [`a.md`](a.md)）はコードの番兵がリンクの番兵の中に入れ子になる。
        // 昇順で復元すると入れ子の番兵を展開する前にリンクへ書き戻してしまい、番兵が生の \x01 のまま
        // 残ってしまう。降順なら外側（リンク）を先に展開して中の番兵を text 上に露出させ、続く
        // イテレーションでそれを解決できる。
        for (var i = spans.Count - 1; i >= 0; i--)
            text = text.Replace($"\x01{i}\x01", spans[i]);

        return text;
    }

    // 地の文に裸で書かれた http(s) URL をリンク化する（GFM の autolink 相当）。すでに [text](url) や
    // 画像として書かれた URL はこの時点で Stash 済みプレースホルダに置き換わっているため対象外。
    // 末尾の句読点・閉じ括弧はリンクに含めない（"見て https://a.b/c 。" の句点まで href に入らないように）。
    private static string AutolinkHtml(string match)
    {
        var url = match;
        var trail = "";
        while (url.Length > 0)
        {
            var c = url[^1];
            if (c == ')')
            {
                if (url.Count(ch => ch == ')') <= url.Count(ch => ch == '('))
                    break; // 開き括弧と対応していれば URL の一部として残す
            }
            else if (")]}>.,;:!?、。」』’”\"'".IndexOf(c) < 0)
            {
                break;
            }
            trail = c + trail;
            url = url[..^1];
        }
        return $"<a href=\"{EncodeAttribute(SanitizeUrl(url))}\">{Encode(url)}</a>{trail}";
    }

    private static string[] SplitRow(string line)
    {
        var s = line.Trim();
        if (s.StartsWith("|")) s = s[1..];
        if (s.EndsWith("|")) s = s[..^1];
        return s.Split('|').Select(c => c.Trim()).ToArray();
    }

    // テーブル区切り行の1セル（例 ":---", "---:", ":---:", "---"）から列揃えを読む。
    // 明示指定が無ければ null（既定の左揃えは CSS 側の text-align: left に任せる）。
    private static string? ColumnAlign(string sepCell)
    {
        var c = sepCell.Trim();
        var left = c.StartsWith(":");
        var right = c.EndsWith(":");
        return (left, right) switch
        {
            (true, true) => "center",
            (false, true) => "right",
            (true, false) => "left",
            _ => null,
        };
    }

    /// <summary>
    /// コードフェンスの言語識別子（```csharp 等）を <see cref="SyntaxEngine"/> の言語判定に使う
    /// 拡張子へマッピングする。Loomo のエディタと同じ字句解析器を使い回すことで、プレビューの
    /// コードブロックにもエディタと同じシンタックスハイライトを適用する。
    /// </summary>
    private static readonly Dictionary<string, string> LanguageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = ".cs", ["cs"] = ".cs", ["c#"] = ".cs",
        ["javascript"] = ".js", ["js"] = ".js", ["jsx"] = ".jsx",
        ["typescript"] = ".ts", ["ts"] = ".ts", ["tsx"] = ".tsx",
        ["python"] = ".py", ["py"] = ".py",
        ["json"] = ".json", ["jsonc"] = ".json",
        ["yaml"] = ".yaml", ["yml"] = ".yaml",
        ["toml"] = ".toml",
        ["xml"] = ".xml", ["html"] = ".html", ["htm"] = ".html", ["svg"] = ".svg", ["xaml"] = ".xaml",
        ["css"] = ".css", ["scss"] = ".scss", ["less"] = ".less",
        ["sql"] = ".sql",
        ["c"] = ".c", ["cpp"] = ".cpp", ["c++"] = ".cpp", ["h"] = ".h", ["hpp"] = ".hpp",
        ["go"] = ".go", ["golang"] = ".go",
        ["rust"] = ".rs", ["rs"] = ".rs",
        ["sh"] = ".sh", ["bash"] = ".sh", ["shell"] = ".sh", ["zsh"] = ".sh",
        ["powershell"] = ".ps1", ["ps1"] = ".ps1", ["pwsh"] = ".ps1",
        ["batch"] = ".bat", ["bat"] = ".bat", ["cmd"] = ".bat",
        ["markdown"] = ".md", ["md"] = ".md",
    };

    private static readonly Dictionary<TokenKind, string> TokenClasses = new()
    {
        [TokenKind.Keyword] = "kw",
        [TokenKind.Type] = "ty",
        [TokenKind.String] = "str",
        [TokenKind.Comment] = "cm",
        [TokenKind.Number] = "num",
        [TokenKind.Preprocessor] = "pp",
        [TokenKind.Attribute] = "at",
        [TokenKind.Function] = "fn",
        // Text / Operator / Identifier は既定の前景色のまま（装飾なし）。
    };

    /// <summary>diff/patch フェンスは <see cref="SyntaxEngine"/> を使わず、行頭記号だけで
    /// 追加／削除／ハンク見出しを色分けする（unified diff の慣習に合わせる）。</summary>
    private static void AppendDiffHighlightedCode(StringBuilder html, List<string> codeLines)
    {
        foreach (var line in codeLines)
        {
            var cls = line switch
            {
                _ when line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) => "diff-meta",
                _ when line.StartsWith("@@", StringComparison.Ordinal) => "diff-hunk",
                _ when line.StartsWith("+", StringComparison.Ordinal) => "diff-add",
                _ when line.StartsWith("-", StringComparison.Ordinal) => "diff-del",
                _ => null,
            };
            html.Append(cls is null ? Encode(line) : $"<span class=\"{cls}\">{Encode(line)}</span>").Append('\n');
        }
    }

    /// <summary>コードフェンスの本文を、言語が判れば <see cref="SyntaxEngine"/> でトークン化して
    /// <c>&lt;span class="tok-*"&gt;</c> 付きで、判らなければプレーンにエンコードして出力する。</summary>
    private static void AppendHighlightedCode(StringBuilder html, string rawLang, List<string> codeLines)
    {
        LineTokens[] tokenLines = [];
        if (!string.IsNullOrWhiteSpace(rawLang) && LanguageExtensions.TryGetValue(rawLang.Trim(), out var ext))
        {
            var engine = new SyntaxEngine();
            engine.DetectLanguage("code" + ext);
            tokenLines = engine.Tokenize(codeLines.ToArray());
        }
        var tokensByLine = tokenLines.ToDictionary(t => t.Line, t => t.Tokens);

        for (var idx = 0; idx < codeLines.Count; idx++)
        {
            var codeLine = codeLines[idx];
            if (tokensByLine.TryGetValue(idx, out var tokens) && tokens.Length > 0)
                AppendHighlightedLine(html, codeLine, tokens);
            else
                html.Append(Encode(codeLine));
            html.Append('\n');
        }
    }

    private static void AppendHighlightedLine(StringBuilder html, string codeLine, SyntaxToken[] tokens)
    {
        var pos = 0;
        foreach (var tok in tokens.OrderBy(t => t.StartColumn))
        {
            if (tok.StartColumn < pos || tok.StartColumn > codeLine.Length)
                continue; // 重なり・範囲外は無視して安全側に倒す
            if (tok.StartColumn > pos)
                html.Append(Encode(codeLine[pos..tok.StartColumn]));
            var end = Math.Min(tok.StartColumn + tok.Length, codeLine.Length);
            var text = codeLine[tok.StartColumn..end];
            html.Append(TokenClasses.TryGetValue(tok.Kind, out var cls)
                ? $"<span class=\"tok-{cls}\">{Encode(text)}</span>"
                : Encode(text));
            pos = end;
        }
        if (pos < codeLine.Length)
            html.Append(Encode(codeLine[pos..]));
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
    // GFM タスクリスト: リスト項目本文の先頭が "[ ] " / "[x] " / "[X] "。
    private static readonly Regex TaskItemRe = new(@"^\[([ xX])\][ \t]+(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableSepRe = new(@"^\|?[\s\-:\|]+\|?$", RegexOptions.Compiled);
    private static readonly Regex CodeSpanRe = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex ImageRe = new(@"!\[([^\]]*)\]\(([^\)]+)\)", RegexOptions.Compiled);
    private static readonly Regex LinkRe = new(@"\[([^\]]+)\]\(([^\)]+)\)", RegexOptions.Compiled);
    // 脚注参照 [^id]（画像・リンクと違い直後に "(" は続かないため、上記2つと衝突しない）。
    private static readonly Regex FootnoteRefRe = new(@"\[\^([^\]\s]+)\]", RegexOptions.Compiled);
    // 脚注定義 [^id]: 説明（行頭限定）。
    private static readonly Regex FootnoteDefRe = new(@"^\[\^([^\]\s]+)\]:[ \t]?(.*)$", RegexOptions.Compiled);
    private static readonly Regex AutolinkRe = new(@"https?://[^\s<>""'　]+", RegexOptions.Compiled);
    private static readonly Regex BoldStarRe = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex BoldUnderRe = new(@"__(.+?)__", RegexOptions.Compiled);
    private static readonly Regex ItalicStarRe = new(@"\*([^\s\*].*?[^\s\*]?)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex ItalicUnderRe = new(@"_([^\s_].*?[^\s_]?)_(?!_)", RegexOptions.Compiled);
    private static readonly Regex StrikeRe = new(@"~~(.+?)~~", RegexOptions.Compiled);

}
