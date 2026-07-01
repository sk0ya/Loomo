using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// JSON（.json / .jsonc）を折りたたみ可能なツリーで表示する EditorSupport 提供者。
/// Markdown プレビューと同じく <see cref="IEditorSupportIncrementalHtmlProvider"/> なので、編集中は
/// 本文（#json-root の中身）だけを差し替えてフル再ナビゲート（＝チカチカ）を避ける。テーマは
/// Markdown プレビューと同じ <c>Appearance.MarkdownPreviewTheme</c> に合わせる。表示専用（書き戻しなし）。
/// </summary>
public sealed class JsonEditorSupport : IEditorSupportIncrementalHtmlProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".json", ".jsonc"];

    public JsonEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"JSON: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => JsonPreviewPage.BuildPage(JsonTreeRenderer.RenderTree(text), DescribeTitle(filePath),
            _settings.Appearance.MarkdownPreviewTheme);

    public string RenderBody(string filePath, string text) => JsonTreeRenderer.RenderTree(text);

    // ページの体裁（対象ファイル・テーマ）だけを鍵にする。本文そのものは含めない＝同じファイルを
    // 同じテーマで編集している間は #json-root の差し替えだけで更新できる。
    public string PageContextKey(string filePath, string text)
        => string.Join("\n", filePath, _settings.Appearance.MarkdownPreviewTheme);
}

/// <summary>
/// JSON テキストを折りたたみツリーの HTML（#json-root の中身）へ変換する純ロジック。
/// パースは寛容（コメント・末尾カンマ許容）で .jsonc も扱う。壊れた JSON（編集途中）は
/// エラー位置と原文を出す。巨大ファイルでブラウザを固めないよう出力ノード数に上限を設ける。
/// </summary>
public static class JsonTreeRenderer
{
    /// <summary>出力するノードの上限。超えたら以降を省略して注記する（巨大 JSON で UI を固めない）。</summary>
    private const int MaxNodes = 100_000;

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 256
    };

    public static string RenderTree(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "<div class=\"empty\">（空のファイル）</div>";

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, ParseOptions);
        }
        catch (JsonException ex)
        {
            return RenderError(text, ex);
        }

        using (doc)
        {
            // JSONパス → ソース行番号。ツリーの各ノードに data-line として埋め、「↦」で Editor の
            // 該当行へカーソルを飛ばせるようにする。壊れた JSON はここまで来ない（RenderError 済み）。
            var lines = BuildLineMap(text);
            var sb = new StringBuilder();
            var budget = MaxNodes;
            WriteNode(sb, keyHtml: null, doc.RootElement, "$", lines, ref budget);
            if (budget <= 0)
                sb.Append("<div class=\"trunc\">… ノードが多すぎるため以降を省略しました</div>");
            return sb.ToString();
        }
    }

    private static void WriteNode(
        StringBuilder sb, string? keyHtml, JsonElement el, string path,
        IReadOnlyDictionary<string, int> lines, ref int budget)
    {
        if (budget <= 0)
            return;
        budget--;

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                WriteContainer(sb, keyHtml, el, '{', '}', path, lines, ref budget);
                break;
            case JsonValueKind.Array:
                WriteContainer(sb, keyHtml, el, '[', ']', path, lines, ref budget);
                break;
            default:
                sb.Append("<div class=\"line\" data-path=\"").Append(MarkdownRenderer.EncodeAttribute(path))
                  .Append('"').Append(LineAttr(lines, path)).Append('>');
                AppendKey(sb, keyHtml);
                AppendLeaf(sb, el);
                AppendActions(sb);
                sb.Append("</div>");
                break;
        }
    }

    private static void WriteContainer(
        StringBuilder sb, string? keyHtml, JsonElement el, char open, char close, string path,
        IReadOnlyDictionary<string, int> lines, ref int budget)
    {
        bool isObject = open == '{';
        int count = isObject ? CountObject(el) : el.GetArrayLength();
        var pathAttr = MarkdownRenderer.EncodeAttribute(path);
        var lineAttr = LineAttr(lines, path);

        // 空のコンテナは折りたためないので 1 行（{ } / [ ]）で出す。
        if (count == 0)
        {
            sb.Append("<div class=\"line\" data-path=\"").Append(pathAttr).Append('"').Append(lineAttr).Append('>');
            AppendKey(sb, keyHtml);
            sb.Append("<span class=\"p\">").Append(open).Append(' ').Append(close).Append("</span>");
            AppendActions(sb);
            sb.Append("</div>");
            return;
        }

        var unit = isObject ? "項目" : "要素";
        sb.Append("<div class=\"node\">")
          .Append("<div class=\"line opening\" data-path=\"").Append(pathAttr).Append('"').Append(lineAttr)
          .Append("><span class=\"caret\"></span>");
        AppendKey(sb, keyHtml);
        sb.Append("<span class=\"p\">").Append(open).Append("</span>")
          .Append("<span class=\"meta\"> ").Append(count).Append(' ').Append(unit).Append("</span>")
          .Append("<span class=\"preview\"> … <span class=\"p\">").Append(close).Append("</span></span>");
        AppendActions(sb);
        sb.Append("</div><div class=\"children\">");

        if (isObject)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (budget <= 0)
                    break;
                var k = "<span class=\"k\">\"" + MarkdownRenderer.Encode(prop.Name) + "\"</span><span class=\"c\">: </span>";
                WriteNode(sb, k, prop.Value, path + Accessor(prop.Name), lines, ref budget);
            }
        }
        else
        {
            var i = 0;
            foreach (var item in el.EnumerateArray())
            {
                if (budget <= 0)
                    break;
                WriteNode(sb, keyHtml: null, item, path + "[" + i + "]", lines, ref budget);
                i++;
            }
        }

        sb.Append("</div><div class=\"line closing\"><span class=\"p\">")
          .Append(close).Append("</span></div></div>");
    }

    private static string LineAttr(IReadOnlyDictionary<string, int> lines, string path)
        => lines.TryGetValue(path, out var l) ? " data-line=\"" + l + "\"" : "";

    private static void AppendKey(StringBuilder sb, string? keyHtml)
    {
        if (keyHtml is not null)
            sb.Append(keyHtml);
    }

    /// <summary>各行の末尾にホバーで現れる操作アイコン（パスをコピー／エディタで開く）。</summary>
    private static void AppendActions(StringBuilder sb)
        => sb.Append("<span class=\"goto\" title=\"エディタでこの位置を開く\">↦</span>")
             .Append("<span class=\"copy\" title=\"JSONパスをコピー\">⧉</span>");

    private static void AppendLeaf(StringBuilder sb, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var s = el.GetString() ?? "";
                sb.Append("<span class=\"s\" data-val=\"").Append(MarkdownRenderer.EncodeAttribute(s))
                  .Append("\" title=\"クリックで値をコピー\">\"")
                  .Append(MarkdownRenderer.Encode(s))
                  .Append("\"</span>");
                break;
            case JsonValueKind.Number:
                var n = el.GetRawText();
                sb.Append("<span class=\"n\" data-val=\"").Append(MarkdownRenderer.EncodeAttribute(n))
                  .Append("\" title=\"クリックで値をコピー\">").Append(MarkdownRenderer.Encode(n)).Append("</span>");
                break;
            case JsonValueKind.True:
                sb.Append("<span class=\"b\" data-val=\"true\" title=\"クリックで値をコピー\">true</span>");
                break;
            case JsonValueKind.False:
                sb.Append("<span class=\"b\" data-val=\"false\" title=\"クリックで値をコピー\">false</span>");
                break;
            default: // Null / Undefined
                sb.Append("<span class=\"z\" data-val=\"null\" title=\"クリックで値をコピー\">null</span>");
                break;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex IdentifierKey =
        new("^[A-Za-z_$][A-Za-z0-9_$]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>JSONパスのアクセサ表記。識別子キーは <c>.key</c>、それ以外は <c>["key"]</c>（エスケープ）。</summary>
    private static string Accessor(string name)
        => IdentifierKey.IsMatch(name)
            ? "." + name
            : "[\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]";

    private static int CountObject(JsonElement obj)
    {
        var n = 0;
        foreach (var _ in obj.EnumerateObject())
            n++;
        return n;
    }

    /// <summary>
    /// JSONパス（<see cref="WriteNode"/> が出すのと同じ表記）→ ソースの 1 始まり行番号。
    /// JsonDocument は位置を持たないので <see cref="Utf8JsonReader"/> でもう一度なめて
    /// 各値トークンの開始バイト位置から行を割り出す。重複キーは先勝ち。
    /// </summary>
    private static Dictionary<string, int> BuildLineMap(string text)
    {
        var map = new Dictionary<string, int>();
        var bytes = Encoding.UTF8.GetBytes(text);

        // 改行（\n）のバイト位置。位置→行は二分探索で求める。
        var newlines = new List<int>();
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\n')
                newlines.Add(i);

        int LineAt(long offset)
        {
            int lo = 0, hi = newlines.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (newlines[mid] < offset) lo = mid + 1;
                else hi = mid;
            }
            return lo + 1;
        }

        var stack = new List<Frame>();
        string PathForValue()
        {
            if (stack.Count == 0)
                return "$";
            var top = stack[^1];
            if (top.IsArray)
                return top.Path + "[" + top.Index++ + "]";
            return top.PendingProp ?? top.Path;
        }

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 256
            });

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        stack[^1].PendingProp = stack[^1].Path + Accessor(reader.GetString()!);
                        break;
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        var p = PathForValue();
                        map.TryAdd(p, LineAt(reader.TokenStartIndex));
                        stack.Add(new Frame { IsArray = reader.TokenType == JsonTokenType.StartArray, Path = p });
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        stack.RemoveAt(stack.Count - 1);
                        break;
                    default: // scalar
                        map.TryAdd(PathForValue(), LineAt(reader.TokenStartIndex));
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // 編集途中などで壊れていれば、ここまでに拾えた分だけ返す（行ジャンプは保険なので欠けてよい）。
        }

        return map;
    }

    private sealed class Frame
    {
        public bool IsArray;
        public string Path = "";
        public int Index;
        public string? PendingProp;
    }

    private static string RenderError(string text, JsonException ex)
    {
        var msg = ex.LineNumber is { } line
            ? $"JSON を解析できません（{line + 1} 行目, 位置 {(ex.BytePositionInLine ?? 0) + 1}）"
            : "JSON を解析できません";
        return "<div class=\"err\"><div class=\"err-head\">" + MarkdownRenderer.Encode(msg) + "</div>"
            + "<pre class=\"raw\">" + MarkdownRenderer.Encode(text) + "</pre></div>";
    }
}

/// <summary>JSON ツリープレビューのフル HTML 文書（テーマ別 CSS と折りたたみ／本文差し替えの JS）。</summary>
internal static class JsonPreviewPage
{
    internal static string BuildPage(string treeBody, string title, string styleName)
    {
        var t = MarkdownRenderer.Encode(title);
        var css = Css(styleName);

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <title>{{t}}</title>
            <style>
            {{css}}
            </style>
            <script>
            (() => {
                const root = () => document.getElementById('json-root');
                const filterInput = () => document.getElementById('json-filter');

                // --- クリップボードへコピー（https://page.loomo は secure context なので Clipboard API 可。失敗時は退避） ---
                function copyText(text) {
                    if (navigator.clipboard?.writeText) {
                        navigator.clipboard.writeText(text).catch(() => fallbackCopy(text));
                    } else {
                        fallbackCopy(text);
                    }
                }
                function fallbackCopy(text) {
                    const ta = document.createElement('textarea');
                    ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
                    document.body.appendChild(ta); ta.select();
                    try { document.execCommand('copy'); } catch (e) {}
                    document.body.removeChild(ta);
                }

                let toastTimer = null;
                function toast(msg) {
                    let el = document.getElementById('json-toast');
                    if (!el) {
                        el = document.createElement('div');
                        el.id = 'json-toast';
                        document.body.appendChild(el);
                    }
                    el.textContent = msg;
                    el.classList.add('show');
                    clearTimeout(toastTimer);
                    toastTimer = setTimeout(() => el.classList.remove('show'), 1400);
                }

                // --- クリック委譲（document へ1回だけ＝本文差し替え後も効く） ---
                document.addEventListener('click', e => {
                    // 0) エディタで開く（行末アイコン）：対応するソース行へカーソルを飛ばしてフォーカス
                    const go = e.target.closest('.goto');
                    if (go) {
                        e.stopPropagation();
                        const line = Number(go.closest('[data-line]')?.getAttribute('data-line')) || 0;
                        if (line && window.chrome?.webview)
                            window.chrome.webview.postMessage({ type: 'jumpToSource', line: line });
                        return;
                    }
                    // 1) パスコピー（行末アイコン）
                    const copy = e.target.closest('.copy');
                    if (copy) {
                        e.stopPropagation();
                        const line = copy.closest('[data-path]');
                        const path = line?.getAttribute('data-path') || '';
                        copyText(path);
                        toast('パスをコピー: ' + path);
                        return;
                    }
                    // 2) 値コピー（葉の値をクリック）
                    const val = e.target.closest('[data-val]');
                    if (val) {
                        e.stopPropagation();
                        copyText(val.getAttribute('data-val') || '');
                        toast('値をコピーしました');
                        return;
                    }
                    // 3) 折りたたみ（開閉行）
                    const opening = e.target.closest('.opening');
                    if (opening) {
                        const node = opening.parentElement;
                        if (node && node.classList.contains('node')) node.classList.toggle('collapsed');
                        return;
                    }
                    // 4) ツールバー（全展開／全折りたたみ）
                    const btn = e.target.closest('[data-act]');
                    if (!btn) return;
                    const collapse = btn.getAttribute('data-act') === 'collapse';
                    root()?.querySelectorAll('.node').forEach(n => n.classList.toggle('collapsed', collapse));
                });

                // --- 絞り込み：キー/値に一致する枝だけ残し、祖先を自動展開してハイライト ---
                function filterLeaf(line, q) {
                    const match = line.textContent.toLowerCase().includes(q);
                    line.style.display = match ? '' : 'none';
                    line.classList.toggle('match', match);
                    return match;
                }
                function filterNode(node, q) {
                    const opening = node.querySelector(':scope > .opening');
                    const wrap = node.querySelector(':scope > .children');
                    const keyText = (opening.querySelector('.k')?.textContent || '').toLowerCase();
                    const selfMatch = keyText.includes(q);
                    let anyChild = false;
                    wrap.querySelectorAll(':scope > .node, :scope > .line').forEach(ch => {
                        const vis = ch.classList.contains('node') ? filterNode(ch, q) : filterLeaf(ch, q);
                        anyChild = anyChild || vis;
                    });
                    const visible = selfMatch || anyChild;
                    node.style.display = visible ? '' : 'none';
                    opening.classList.toggle('match', selfMatch);
                    if (anyChild) node.classList.remove('collapsed'); // 一致を見せるため展開
                    return visible;
                }
                function applyFilter() {
                    const r = root();
                    if (!r) return;
                    const q = (filterInput()?.value || '').trim().toLowerCase();
                    if (!q) {
                        r.querySelectorAll('.node, .line').forEach(el => {
                            el.style.display = '';
                            el.classList.remove('match');
                        });
                        return;
                    }
                    r.querySelectorAll(':scope > .node, :scope > .line').forEach(el => {
                        if (el.classList.contains('node')) filterNode(el, q);
                        else filterLeaf(el, q);
                    });
                }
                document.addEventListener('input', e => {
                    if (e.target.id === 'json-filter') applyFilter();
                });

                // --- 編集中はフル再ナビゲートせず本文だけ差し替える（チカチカ防止）。差し替え後は絞り込みを貼り直す ---
                if (window.chrome?.webview) {
                    window.chrome.webview.addEventListener('message', e => {
                        const d = e.data;
                        if (d && d.type === 'setBody') {
                            const r = root();
                            if (r) { r.innerHTML = d.html; applyFilter(); }
                        }
                    });
                }
            })();
            </script>
            </head>
            <body>
            <div class="toolbar">
                <input id="json-filter" type="search" placeholder="キー・値で絞り込み…" autocomplete="off" spellcheck="false">
                <button data-act="expand">全展開</button>
                <button data-act="collapse">全折りたたみ</button>
            </div>
            <div id="json-root">{{treeBody}}</div>
            </body>
            </html>
            """;
    }

    // テーマ別パレット（bg, fg, panel, border, muted, key, str, num, kw, punct）。Markdown プレビューと
    // 色味をそろえる。styleName の正規化は MarkdownPage と共有。
    private static string Css(string styleName) => MarkdownPage.NormalizeStyle(styleName) switch
    {
        "Light" => Base("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#6E7781",
                        "#0550AE", "#0A3069", "#116329", "#CF222E", "#24292F"),
        "GitHub" => Base("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#6E7781",
                         "#0550AE", "#0A3069", "#116329", "#CF222E", "#24292F"),
        "Dark" => Base("#1E1E1E", "#D4D4D4", "#252526", "#3C3C3C", "#858585",
                       "#9CDCFE", "#CE9178", "#B5CEA8", "#569CD6", "#D4D4D4"),
        "Nord" => Base("#2E3440", "#D8DEE9", "#3B4252", "#4C566A", "#616E88",
                       "#88C0D0", "#A3BE8C", "#B48EAD", "#81A1C1", "#D8DEE9"),
        "TokyoNight" => Base("#1A1B26", "#C0CAF5", "#24283B", "#414868", "#565F89",
                             "#7AA2F7", "#9ECE6A", "#E0AF68", "#BB9AF7", "#C0CAF5"),
        "OneDark" => Base("#282C34", "#ABB2BF", "#21252B", "#3E4451", "#5C6370",
                          "#61AFEF", "#98C379", "#D19A66", "#C678DD", "#ABB2BF"),
        "SolarizedDark" => Base("#002B36", "#93A1A1", "#073642", "#586E75", "#586E75",
                                "#268BD2", "#859900", "#CB4B16", "#2AA198", "#93A1A1"),
        "Monokai" => Base("#272822", "#F8F8F2", "#3E3D32", "#49483E", "#75715E",
                          "#66D9EF", "#E6DB74", "#AE81FF", "#F92672", "#F8F8F2"),
        _ => Base("#282A36", "#F8F8F2", "#1E1F29", "#44475A", "#6272A4",
                  "#8BE9FD", "#F1FA8C", "#BD93F9", "#FF79C6", "#F8F8F2"),
    };

    private static string Base(
        string bg, string fg, string panel, string border, string muted,
        string key, string str, string num, string kw, string punct)
    {
        var scheme = bg == "#FFFFFF" ? "light" : "dark";
        return $$"""
            * { box-sizing: border-box; margin: 0; padding: 0; }
            html { color-scheme: {{scheme}}; scrollbar-color: {{border}} transparent; scrollbar-width: thin; }
            ::-webkit-scrollbar { width: 12px; height: 12px; }
            ::-webkit-scrollbar-track { background: transparent; }
            ::-webkit-scrollbar-thumb {
                background: {{border}}; border-radius: 8px;
                border: 3px solid {{bg}}; background-clip: padding-box;
            }
            ::-webkit-scrollbar-thumb:hover { background: {{muted}}; background-clip: padding-box; }
            body {
                background: {{bg}}; color: {{fg}};
                font-family: 'Cascadia Code', Consolas, monospace;
                font-size: 13px; line-height: 1.55;
                padding: 8px 12px 40px;
            }
            .toolbar {
                position: sticky; top: 0; z-index: 5;
                display: flex; gap: 6px; padding: 4px 0 8px; background: {{bg}};
                border-bottom: 1px solid {{border}}; margin-bottom: 8px;
            }
            .toolbar button {
                font: inherit; font-size: 11px; color: {{muted}};
                background: {{panel}}; border: 1px solid {{border}};
                border-radius: 4px; padding: 2px 10px; cursor: pointer;
            }
            .toolbar button:hover { color: {{fg}}; border-color: {{muted}}; }
            .toolbar input {
                flex: 1; min-width: 0; font: inherit; font-size: 12px;
                color: {{fg}}; background: {{panel}}; border: 1px solid {{border}};
                border-radius: 4px; padding: 2px 8px; outline: none;
            }
            .toolbar input::placeholder { color: {{muted}}; }
            .toolbar input:focus { border-color: {{muted}}; }
            #json-root { white-space: nowrap; }
            .line { white-space: pre; }
            .opening { cursor: pointer; }
            .copy, .goto {
                opacity: 0; cursor: pointer; margin-left: 10px;
                color: {{muted}}; user-select: none; font-size: 0.95em;
            }
            .goto { margin-left: 12px; }
            .line:hover > .copy, .line:hover > .goto { opacity: 0.65; }
            .copy:hover, .goto:hover { opacity: 1 !important; color: {{key}}; }
            [data-val] { cursor: pointer; }
            .match { background: color-mix(in srgb, {{key}} 22%, transparent); border-radius: 3px; }
            #json-toast {
                position: fixed; right: 14px; bottom: 14px; z-index: 20;
                max-width: 80%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
                background: {{panel}}; color: {{fg}}; border: 1px solid {{border}};
                border-radius: 6px; padding: 6px 12px; font-size: 12px;
                opacity: 0; transform: translateY(6px); transition: opacity .15s, transform .15s;
                pointer-events: none;
            }
            #json-toast.show { opacity: 0.97; transform: translateY(0); }
            .children { padding-left: 1.4em; border-left: 1px solid {{border}}; margin-left: 0.25em; }
            .caret {
                display: inline-block; width: 1em; color: {{muted}};
                text-align: center; user-select: none;
            }
            .caret::before { content: '▾'; }
            .node.collapsed > .opening .caret::before { content: '▸'; }
            .node.collapsed > .children { display: none; }
            .node.collapsed > .closing { display: none; }
            .preview { display: none; color: {{muted}}; }
            .node.collapsed > .opening .preview { display: inline; }
            .k { color: {{key}}; }
            .c, .p { color: {{punct}}; }
            .s { color: {{str}}; }
            .n { color: {{num}}; }
            .b, .z { color: {{kw}}; }
            .meta { color: {{muted}}; font-size: 0.85em; }
            .empty { color: {{muted}}; padding: 12px; }
            .err { padding: 4px; }
            .err-head {
                color: {{kw}}; background: {{panel}};
                border: 1px solid {{border}}; border-radius: 4px;
                padding: 8px 12px; margin-bottom: 10px;
            }
            .raw {
                white-space: pre-wrap; word-break: break-word;
                color: {{muted}}; background: {{panel}};
                border: 1px solid {{border}}; border-radius: 4px;
                padding: 10px 12px; font-size: 0.9em;
            }
            """;
    }
}
