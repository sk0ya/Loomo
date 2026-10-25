using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// パッチ（.patch／.diff）を、ファイルごとの差分として読む EditorSupport 提供者。§24.68。
/// <para>中身は同じ文字で、見せ方だけを変える——ファイルの見出し（増減つき）、ハンクごとに旧／新の行番号、追加と削除の色、
/// 書き換えた行の中のどこが変わったか（§24.25 と同じ <see cref="LinePairing"/>＋<see cref="InlineDiff"/>）。当てる口は
/// エディタの右クリック「このパッチを作業ツリーへ当てる」（§24.38.1）にあるので、ここは読むだけ。</para>
/// <para>ログ・JSON と同じく <see cref="IEditorSupportIncrementalHtmlProvider"/> なので、編集中は本文だけを差し替える。</para>
/// </summary>
public sealed class PatchEditorSupport : IEditorSupportIncrementalHtmlProvider
{
    private readonly LoomoSettings _settings;
    private static readonly string[] Extensions = [".patch", ".diff"];

    public PatchEditorSupport(LoomoSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"Patch: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => PatchPreviewPage.BuildPage(RenderBody(filePath, text), DescribeTitle(filePath),
            _settings.Appearance.MarkdownPreviewTheme);

    public string RenderBody(string filePath, string text) => PatchRenderer.Render(PatchDocument.Parse(text));

    public string PageContextKey(string filePath, string text)
        => string.Join("\n", filePath, _settings.Appearance.MarkdownPreviewTheme);
}

/// <summary>読み取ったパッチを HTML（#patch-root の中身）にする純ロジック。</summary>
public static class PatchRenderer
{
    /// <summary>描く行の上限（全ファイル合計）。超えたら以降のハンクを省いて注記する。</summary>
    public const int MaxLines = 20_000;

    public static string Render(PatchDocument patch)
    {
        if (!patch.HasChanges)
            return "<div class=\"empty\">差分が見つかりません（<code>diff --git</code>・<code>---</code>／<code>+++</code>・<code>@@</code> の見出しを探しました）</div>";

        var sb = new StringBuilder();
        var added = patch.Files.Sum(f => f.Added);
        var removed = patch.Files.Sum(f => f.Removed);
        sb.Append("<div class=\"summary\">").Append(patch.Files.Count).Append(" ファイル")
          .Append(" <span class=\"plus\">+").Append(added).Append("</span>")
          .Append(" <span class=\"minus\">−").Append(removed).Append("</span></div>");

        if (patch.Preamble.Count > 0)
        {
            sb.Append("<pre class=\"preamble\">")
              .Append(MarkdownRenderer.Encode(string.Join("\n", patch.Preamble)))
              .Append("</pre>");
        }

        if (patch.Files.Count > 1)
        {
            sb.Append("<ul class=\"toc\">");
            for (var i = 0; i < patch.Files.Count; i++)
            {
                var f = patch.Files[i];
                sb.Append("<li><a href=\"#f").Append(i).Append("\">").Append(MarkdownRenderer.Encode(f.DisplayPath))
                  .Append("</a> <span class=\"plus\">+").Append(f.Added).Append("</span> <span class=\"minus\">−")
                  .Append(f.Removed).Append("</span></li>");
            }
            sb.Append("</ul>");
        }

        var budget = MaxLines;
        for (var i = 0; i < patch.Files.Count; i++)
        {
            var file = patch.Files[i];
            sb.Append("<section class=\"file\" id=\"f").Append(i).Append("\"><div class=\"fhead\">")
              .Append("<span class=\"fname\">").Append(MarkdownRenderer.Encode(file.DisplayPath)).Append("</span>")
              .Append(" <span class=\"tag\">").Append(Kind(file)).Append("</span>")
              .Append(" <span class=\"plus\">+").Append(file.Added).Append("</span>")
              .Append(" <span class=\"minus\">−").Append(file.Removed).Append("</span></div>");
            foreach (var meta in file.Meta.Where(m => !m.StartsWith("new file mode", StringComparison.Ordinal)
                                                      && !m.StartsWith("deleted file mode", StringComparison.Ordinal)))
                sb.Append("<div class=\"meta\">").Append(MarkdownRenderer.Encode(meta)).Append("</div>");

            foreach (var hunk in file.Hunks)
            {
                if (budget <= 0)
                {
                    sb.Append("<div class=\"trunc\">… 行が多いため、ここから先は省きました（").Append(MaxLines).Append(" 行まで）</div>");
                    break;
                }
                sb.Append("<div class=\"hunk\">").Append(MarkdownRenderer.Encode(hunk.Header)).Append("</div><table class=\"lines\">");
                RenderLines(sb, hunk.Lines, ref budget);
                sb.Append("</table>");
            }
            sb.Append("</section>");
            if (budget <= 0 && i < patch.Files.Count - 1)
            {
                sb.Append("<div class=\"trunc\">… 残り ").Append(patch.Files.Count - 1 - i).Append(" ファイルは省きました</div>");
                break;
            }
        }
        return sb.ToString();
    }

    private static string Kind(PatchFile file) => (file.OldPath, file.NewPath) switch
    {
        _ when file.IsBinary => "バイナリ",
        (null, not null) => "追加",
        (not null, null) => "削除",
        ({ } o, { } n) when o != n => "名前の変更",
        _ => "変更",
    };

    /// <summary>行を描く。削除→追加のかたまりは似ている行どうしを対にし、対の行には変わった範囲を濃く敷く。</summary>
    private static void RenderLines(StringBuilder sb, IReadOnlyList<PatchLine> lines, ref int budget)
    {
        var i = 0;
        while (i < lines.Count && budget > 0)
        {
            if (lines[i].Kind != PatchLineKind.Removed && lines[i].Kind != PatchLineKind.Added)
            {
                Row(sb, lines[i], null);
                budget--;
                i++;
                continue;
            }
            var removed = new List<PatchLine>();
            var added = new List<PatchLine>();
            while (i < lines.Count && lines[i].Kind == PatchLineKind.Removed) removed.Add(lines[i++]);
            while (i < lines.Count && lines[i].Kind == PatchLineKind.Added) added.Add(lines[i++]);

            var spans = new Dictionary<PatchLine, IReadOnlyList<TextSpan>>();
            if (removed.Count > 0 && added.Count > 0)
            {
                foreach (var (r, a) in LinePairing.Pair(removed.Select(l => l.Text).ToList(), added.Select(l => l.Text).ToList()))
                {
                    if (r < 0 || a < 0 || InlineDiff.Compute(removed[r].Text, added[a].Text) is not { } change)
                        continue;
                    spans[removed[r]] = change.Left;
                    spans[added[a]] = change.Right;
                }
            }
            // 統合表示の並び（削除を先にまとめ、追加を後に）は git と同じ。
            foreach (var line in removed.Concat(added))
            {
                if (budget-- <= 0) break;
                Row(sb, line, spans.GetValueOrDefault(line));
            }
        }
    }

    private static void Row(StringBuilder sb, PatchLine line, IReadOnlyList<TextSpan>? spans)
    {
        var (cls, sign) = line.Kind switch
        {
            PatchLineKind.Added => ("add", "+"),
            PatchLineKind.Removed => ("del", "−"),
            PatchLineKind.NoNewline => ("note", ""),
            _ => ("ctx", " "),
        };
        sb.Append("<tr class=\"").Append(cls).Append("\"><td class=\"ln\">").Append(line.OldLine?.ToString() ?? "")
          .Append("</td><td class=\"ln\">").Append(line.NewLine?.ToString() ?? "")
          .Append("</td><td class=\"sg\">").Append(sign).Append("</td><td class=\"tx\">");
        AppendText(sb, line.Text, spans);
        sb.Append("</td></tr>");
    }

    internal static void AppendText(StringBuilder sb, string text, IReadOnlyList<TextSpan>? spans)
    {
        if (text.Length == 0)
        {
            sb.Append(' ');
            return;
        }
        if (spans is null || spans.Count == 0)
        {
            sb.Append(MarkdownRenderer.Encode(text));
            return;
        }
        var at = 0;
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            var start = Math.Clamp(span.Start, at, text.Length);
            var end = Math.Clamp(span.End, start, text.Length);
            sb.Append(MarkdownRenderer.Encode(text[at..start]));
            if (end > start)
                sb.Append("<span class=\"chg\">").Append(MarkdownRenderer.Encode(text[start..end])).Append("</span>");
            at = end;
        }
        sb.Append(MarkdownRenderer.Encode(text[at..]));
    }
}

/// <summary>パッチビューアのフル HTML 文書。配色は Markdown プレビューのテーマに合わせる（<see cref="LogPreviewPage"/> と同じ並び）。</summary>
internal static class PatchPreviewPage
{
    internal static string BuildPage(string body, string title, string styleName)
    {
        var t = MarkdownRenderer.Encode(title);
        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <title>{{t}}</title>
            <style>
            {{Css(styleName)}}
            </style>
            <script>
            (() => {
                if (window.chrome?.webview) {
                    window.chrome.webview.addEventListener('message', e => {
                        const d = e.data;
                        if (d && d.type === 'setBody') {
                            const r = document.getElementById('patch-root');
                            if (r) r.innerHTML = d.html;
                        }
                    });
                }
            })();
            </script>
            </head>
            <body>
            <div id="patch-root">{{body}}</div>
            </body>
            </html>
            """;
    }

    // テーマ別パレット（bg, fg, panel, border, muted, add, del）。
    private static string Css(string styleName) => MarkdownPage.NormalizeStyle(styleName) switch
    {
        "Light" or "GitHub" => Base("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#6E7781", "#1A7F37", "#CF222E"),
        "Dark" => Base("#1E1E1E", "#D4D4D4", "#252526", "#3C3C3C", "#858585", "#4EC9B0", "#F48771"),
        "Nord" => Base("#2E3440", "#D8DEE9", "#3B4252", "#4C566A", "#616E88", "#A3BE8C", "#BF616A"),
        "TokyoNight" => Base("#1A1B26", "#C0CAF5", "#24283B", "#414868", "#565F89", "#9ECE6A", "#F7768E"),
        "OneDark" => Base("#282C34", "#ABB2BF", "#21252B", "#3E4451", "#5C6370", "#98C379", "#E06C75"),
        "SolarizedDark" => Base("#002B36", "#93A1A1", "#073642", "#586E75", "#586E75", "#859900", "#DC322F"),
        "Monokai" => Base("#272822", "#F8F8F2", "#3E3D32", "#49483E", "#75715E", "#A6E22E", "#F92672"),
        _ => Base("#282A36", "#F8F8F2", "#1E1F29", "#44475A", "#6272A4", "#50FA7B", "#FF5555"),
    };

    private static string Base(string bg, string fg, string panel, string border, string muted, string add, string del)
    {
        var scheme = bg == "#FFFFFF" ? "light" : "dark";
        return $$"""
            * { box-sizing: border-box; margin: 0; padding: 0; }
            html { color-scheme: {{scheme}}; scrollbar-color: {{border}} transparent; scrollbar-width: thin; }
            body {
                background: {{bg}}; color: {{fg}};
                font-family: 'Cascadia Code', Consolas, monospace; font-size: 12.5px; line-height: 1.5;
                padding: 8px 0 40px;
            }
            .summary { padding: 2px 12px 6px; color: {{muted}}; }
            .plus { color: {{add}}; } .minus { color: {{del}}; }
            .preamble { margin: 0 12px 8px; padding: 6px 8px; background: {{panel}}; border: 1px solid {{border}};
                border-radius: 4px; white-space: pre-wrap; color: {{muted}}; }
            .toc { list-style: none; padding: 0 12px 8px; }
            .toc a { color: {{fg}}; text-decoration: none; } .toc a:hover { text-decoration: underline; }
            .file { margin: 0 0 12px; border-top: 1px solid {{border}}; }
            .fhead { position: sticky; top: 0; z-index: 2; padding: 4px 12px; background: {{panel}};
                border-bottom: 1px solid {{border}}; }
            .fname { font-weight: 600; }
            .tag { font-size: 10.5px; color: {{muted}}; border: 1px solid {{border}}; border-radius: 3px; padding: 0 4px; }
            .meta { padding: 0 12px; color: {{muted}}; font-size: 11.5px; }
            .hunk { padding: 2px 12px; color: {{muted}}; background: color-mix(in srgb, {{border}} 35%, transparent); }
            table.lines { border-collapse: collapse; width: 100%; }
            td { vertical-align: top; }
            td.ln { width: 1%; min-width: 4ch; padding: 0 6px; text-align: right; color: {{muted}}; opacity: .7;
                user-select: none; white-space: nowrap; }
            td.sg { width: 1%; padding: 0 4px; user-select: none; color: {{muted}}; }
            td.tx { white-space: pre-wrap; word-break: break-word; padding-right: 12px; }
            tr.add { background: color-mix(in srgb, {{add}} 14%, transparent); }
            tr.del { background: color-mix(in srgb, {{del}} 14%, transparent); }
            tr.add td.sg { color: {{add}}; } tr.del td.sg { color: {{del}}; }
            tr.add .chg { background: color-mix(in srgb, {{add}} 38%, transparent); border-radius: 2px; }
            tr.del .chg { background: color-mix(in srgb, {{del}} 38%, transparent); border-radius: 2px; }
            tr.note td.tx { color: {{muted}}; font-style: italic; }
            .empty { color: {{muted}}; padding: 16px; }
            .trunc { color: {{muted}}; padding: 8px 12px; font-style: italic; }
            """;
    }
}
