using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Ai;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ログ（.log）を、行ごとに重大度レベル（ERROR/WARN/INFO/DEBUG/TRACE/FATAL）を判定して色分け表示する
/// EditorSupport 提供者。Markdown/JSON プレビューと同じく <see cref="IEditorSupportIncrementalHtmlProvider"/>
/// なので、編集中は本文（#log-root の中身）だけを差し替えてフル再ナビゲート（＝チカチカ）を避ける。
/// テーマは Markdown プレビューと同じ <c>Appearance.MarkdownPreviewTheme</c> に合わせる。表示専用（書き戻しなし）。
/// </summary>
public sealed class LogEditorSupport : IEditorSupportIncrementalHtmlProvider
{
    private readonly AiSettings _settings;
    private static readonly string[] Extensions = [".log"];

    public LogEditorSupport(AiSettings settings) => _settings = settings;

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public string DescribeTitle(string filePath) => $"Log: {Path.GetFileName(filePath)}";

    public string RenderHtml(string filePath, string text)
        => LogPreviewPage.BuildPage(LogRenderer.RenderLines(text), DescribeTitle(filePath),
            _settings.Appearance.MarkdownPreviewTheme);

    public string RenderBody(string filePath, string text) => LogRenderer.RenderLines(text);

    // ページの体裁（対象ファイル・テーマ）だけを鍵にする。本文そのものは含めない＝同じファイルを
    // 同じテーマで編集している間は #log-root の差し替えだけで更新できる。
    public string PageContextKey(string filePath, string text)
        => string.Join("\n", filePath, _settings.Appearance.MarkdownPreviewTheme);
}

/// <summary>
/// ログ1行の重大度レベル。<c>none</c> は判定できない（本文・継続行など）。
/// </summary>
public enum LogLevel
{
    None,
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

/// <summary>
/// ログ行から重大度レベルを推定する純ロジック。<c>[ERROR]</c> / <c> WARN </c> / <c>INFO:</c> /
/// <c>level=error</c> といった、よくある表記を大文字小文字を問わず拾う。判定できない行（本文・スタック
/// トレースの継続など）は <see cref="LogLevel.None"/>。強いレベル（FATAL/ERROR）から順に見て先勝ちで返す。
/// </summary>
public static class LogLineClassifier
{
    // レベル語を「区切り（行頭・空白・記号 [ ( < : = |）」に挟まれたトークンとして拾う。素の単語境界だと
    // "errorCode" のような別語まで拾ってしまうので、ログでレベルを囲む典型的な区切りに限定する。
    private static readonly (LogLevel Level, Regex Pattern)[] Patterns =
    [
        (LogLevel.Fatal, Make("FATAL|CRITICAL|CRIT")),
        (LogLevel.Error, Make("ERROR|ERR")),
        (LogLevel.Warn,  Make("WARNING|WARN")),
        (LogLevel.Info,  Make("INFORMATION|INFO")),
        (LogLevel.Debug, Make("DEBUG|DBG")),
        (LogLevel.Trace, Make("VERBOSE|TRACE|TRC")),
    ];

    private static Regex Make(string alternation)
        => new($@"(?<![A-Za-z0-9])(?:{alternation})(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static LogLevel Classify(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return LogLevel.None;

        // 行の先頭側だけを見る（メッセージ本文にたまたま "error" 等が出ても引っ張られにくくする）。
        var head = line.Length > 200 ? line[..200] : line;
        foreach (var (level, pattern) in Patterns)
            if (pattern.IsMatch(head))
                return level;

        return LogLevel.None;
    }

    /// <summary>レベル→CSS クラス名（本文とページ CSS で共有する）。</summary>
    public static string CssClass(LogLevel level) => level switch
    {
        LogLevel.Fatal => "lv-fatal",
        LogLevel.Error => "lv-error",
        LogLevel.Warn => "lv-warn",
        LogLevel.Info => "lv-info",
        LogLevel.Debug => "lv-debug",
        LogLevel.Trace => "lv-trace",
        _ => "lv-none",
    };

    /// <summary>レベル→フィルタ用の識別子（data-level 属性・トグルボタンの data-level と対応）。</summary>
    public static string Token(LogLevel level) => level switch
    {
        LogLevel.Fatal => "fatal",
        LogLevel.Error => "error",
        LogLevel.Warn => "warn",
        LogLevel.Info => "info",
        LogLevel.Debug => "debug",
        LogLevel.Trace => "trace",
        _ => "none",
    };
}

/// <summary>
/// ログテキストを、行ごとに色分けした HTML（#log-root の中身）へ変換する純ロジック。
/// 巨大ログでブラウザを固めないよう出力行数に上限を設ける。行番号列＋レベル色付き本文を等幅で並べる。
/// </summary>
public static class LogRenderer
{
    /// <summary>出力する行の上限。超えたら以降を省略して注記する（巨大ログで UI を固めない）。</summary>
    private const int MaxLines = 50_000;

    public static string RenderLines(string text)
    {
        // 全体が空白のみ（空ファイル・改行だけ等）は「空」として扱う。実データ行が1行でもあれば描く。
        if (string.IsNullOrWhiteSpace(text))
            return "<div class=\"empty\">（空のファイル）</div>";

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // 末尾の改行由来の空要素は行として数えない（実データ行だけ描く）。
        var count = lines.Length;
        if (count > 0 && lines[^1].Length == 0)
            count--;

        if (count == 0)
            return "<div class=\"empty\">（空のファイル）</div>";

        var shown = Math.Min(count, MaxLines);
        var sb = new StringBuilder(shown * 64);

        for (var i = 0; i < shown; i++)
        {
            var raw = lines[i];
            var level = LogLineClassifier.Classify(raw);
            var cls = LogLineClassifier.CssClass(level);
            var token = LogLineClassifier.Token(level);

            sb.Append("<div class=\"logline ").Append(cls).Append("\" data-level=\"").Append(token).Append("\">")
              .Append("<span class=\"ln\">").Append(i + 1).Append("</span>")
              .Append("<span class=\"lt\">").Append(MarkdownRenderer.Encode(raw.Length == 0 ? " " : raw))
              .Append("</span></div>");
        }

        if (count > shown)
            sb.Append("<div class=\"trunc\">… 行が多すぎるため先頭 ").Append(shown)
              .Append(" 行のみ表示しています（全 ").Append(count).Append(" 行）</div>");

        return sb.ToString();
    }
}

/// <summary>ログビューアのフル HTML 文書（テーマ別 CSS と、テキスト絞り込み／レベル表示トグルの JS）。</summary>
internal static class LogPreviewPage
{
    internal static string BuildPage(string logBody, string title, string styleName)
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
                const root = () => document.getElementById('log-root');
                const filterInput = () => document.getElementById('log-filter');
                // 非表示にするレベル（トグルで OFF にしたもの）。
                const hidden = new Set();

                // --- 絞り込み：テキスト一致＋レベル表示トグルの両方を満たす行だけ残す ---
                function applyFilter() {
                    const r = root();
                    if (!r) return;
                    const q = (filterInput()?.value || '').trim().toLowerCase();
                    r.querySelectorAll('.logline').forEach(line => {
                        const level = line.getAttribute('data-level') || 'none';
                        const textMatch = !q || line.textContent.toLowerCase().includes(q);
                        const levelOn = !hidden.has(level);
                        line.style.display = (textMatch && levelOn) ? '' : 'none';
                    });
                }

                // レベルトグルボタンの押下状態を見た目へ反映。
                function syncToggles() {
                    document.querySelectorAll('.lvl-toggle').forEach(btn => {
                        const level = btn.getAttribute('data-level');
                        btn.classList.toggle('off', hidden.has(level));
                    });
                }

                document.addEventListener('click', e => {
                    const btn = e.target.closest('.lvl-toggle');
                    if (!btn) return;
                    const level = btn.getAttribute('data-level');
                    if (hidden.has(level)) hidden.delete(level);
                    else hidden.add(level);
                    syncToggles();
                    applyFilter();
                });

                document.addEventListener('input', e => {
                    if (e.target.id === 'log-filter') applyFilter();
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
                <input id="log-filter" type="search" placeholder="本文で絞り込み…" autocomplete="off" spellcheck="false">
                <div class="toggles">
                    <button class="lvl-toggle lv-fatal" data-level="fatal">FATAL</button>
                    <button class="lvl-toggle lv-error" data-level="error">ERROR</button>
                    <button class="lvl-toggle lv-warn" data-level="warn">WARN</button>
                    <button class="lvl-toggle lv-info" data-level="info">INFO</button>
                    <button class="lvl-toggle lv-debug" data-level="debug">DEBUG</button>
                    <button class="lvl-toggle lv-trace" data-level="trace">TRACE</button>
                </div>
            </div>
            <div id="log-root">{{logBody}}</div>
            </body>
            </html>
            """;
    }

    // テーマ別パレット（bg, fg, panel, border, muted, error, warn, info, debug）。Markdown/JSON プレビューと
    // 色味をそろえる。styleName の正規化は MarkdownPage と共有。
    private static string Css(string styleName) => MarkdownPage.NormalizeStyle(styleName) switch
    {
        "Light" => Base("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#6E7781",
                        "#CF222E", "#9A6700", "#0550AE", "#6E7781"),
        "GitHub" => Base("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#6E7781",
                         "#CF222E", "#9A6700", "#0550AE", "#6E7781"),
        "Dark" => Base("#1E1E1E", "#D4D4D4", "#252526", "#3C3C3C", "#858585",
                       "#F48771", "#CCA700", "#4FC1FF", "#808080"),
        "Nord" => Base("#2E3440", "#D8DEE9", "#3B4252", "#4C566A", "#616E88",
                       "#BF616A", "#EBCB8B", "#88C0D0", "#616E88"),
        "TokyoNight" => Base("#1A1B26", "#C0CAF5", "#24283B", "#414868", "#565F89",
                             "#F7768E", "#E0AF68", "#7AA2F7", "#565F89"),
        "OneDark" => Base("#282C34", "#ABB2BF", "#21252B", "#3E4451", "#5C6370",
                          "#E06C75", "#E5C07B", "#61AFEF", "#5C6370"),
        "SolarizedDark" => Base("#002B36", "#93A1A1", "#073642", "#586E75", "#586E75",
                                "#DC322F", "#B58900", "#268BD2", "#586E75"),
        "Monokai" => Base("#272822", "#F8F8F2", "#3E3D32", "#49483E", "#75715E",
                          "#F92672", "#E6DB74", "#66D9EF", "#75715E"),
        _ => Base("#282A36", "#F8F8F2", "#1E1F29", "#44475A", "#6272A4",
                  "#FF5555", "#F1FA8C", "#8BE9FD", "#6272A4"),
    };

    private static string Base(
        string bg, string fg, string panel, string border, string muted,
        string error, string warn, string info, string debug)
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
                font-size: 12.5px; line-height: 1.5;
                padding: 0 0 40px;
            }
            .toolbar {
                position: sticky; top: 0; z-index: 5;
                display: flex; gap: 8px; align-items: center; flex-wrap: wrap;
                padding: 6px 10px; background: {{bg}};
                border-bottom: 1px solid {{border}};
            }
            .toolbar input {
                flex: 1; min-width: 120px; font: inherit; font-size: 12px;
                color: {{fg}}; background: {{panel}}; border: 1px solid {{border}};
                border-radius: 4px; padding: 3px 8px; outline: none;
            }
            .toolbar input::placeholder { color: {{muted}}; }
            .toolbar input:focus { border-color: {{muted}}; }
            .toggles { display: flex; gap: 4px; }
            .lvl-toggle {
                font: inherit; font-size: 10.5px; font-weight: 600;
                background: {{panel}}; border: 1px solid {{border}};
                border-radius: 4px; padding: 2px 7px; cursor: pointer; user-select: none;
            }
            .lvl-toggle.off { opacity: 0.4; text-decoration: line-through; }

            #log-root { padding: 4px 0; }
            .logline {
                display: flex; white-space: pre; padding: 0 10px;
                border-left: 3px solid transparent;
            }
            .logline:hover { background: {{panel}}; }
            .ln {
                flex: 0 0 auto; width: 5ch; margin-right: 12px; text-align: right;
                color: {{muted}}; user-select: none; opacity: 0.7;
            }
            .lt { flex: 1 1 auto; white-space: pre-wrap; word-break: break-word; }

            /* レベル別の配色。トグルボタンにも同じクラスが付くので、ボタンの文字色も色分けされる。 */
            .lv-fatal { color: {{error}}; }
            .logline.lv-fatal { border-left-color: {{error}}; background: color-mix(in srgb, {{error}} 14%, transparent); font-weight: 600; }
            .lv-error { color: {{error}}; }
            .logline.lv-error { border-left-color: {{error}}; }
            .lv-warn { color: {{warn}}; }
            .logline.lv-warn { border-left-color: {{warn}}; }
            .lv-info { color: {{info}}; }
            .logline.lv-info .lt { color: {{fg}}; }
            .lv-debug { color: {{debug}}; }
            .logline.lv-debug .lt { color: {{muted}}; }
            .lv-trace { color: {{muted}}; }
            .logline.lv-trace .lt { color: {{muted}}; opacity: 0.85; }
            .logline.lv-none .lt { color: {{fg}}; }
            /* トグルボタンの INFO/none は既定文字色（色分けが無いレベル） */
            .lvl-toggle.lv-info { color: {{info}}; }

            .empty { color: {{muted}}; padding: 16px; }
            .trunc { color: {{muted}}; padding: 10px; font-style: italic; }
            """;
    }
}
