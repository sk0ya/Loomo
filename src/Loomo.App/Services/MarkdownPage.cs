using System;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

/// <summary>Markdown プレビューのフル HTML 文書の組み立て（テーマ別 CSS とスクロール同期／mermaid の JS を含む）。
/// Markdown 本体のパースは <see cref="MarkdownRenderer"/>。</summary>
internal static class MarkdownPage
{
    internal static string BuildPage(string body, string? title, string styleName, string? baseHref = null)
    {
        var t = title != null ? MarkdownRenderer.Encode(title) : "Preview";
        var css = PreviewCss(styleName);
        var baseTag = string.IsNullOrEmpty(baseHref) ? "" : $"<base href=\"{MarkdownRenderer.EncodeAttribute(baseHref)}\">";
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
                const mermaidSrc = 'https://{{MarkdownRenderer.AssetsVirtualHost}}/mermaid.min.js';
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

    // 背景色の明度からネイティブ UI（既定スクロールバー等）の配色モードを決める。
    private static string ColorScheme(string hexBg)
    {
        var hex = hexBg.TrimStart('#');
        if (hex.Length < 6 || !int.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return "dark";
        // 知覚輝度（簡易）。明るければ light。
        return (0.299 * r + 0.587 * g + 0.114 * b) > 140 ? "light" : "dark";
    }

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
            html { color-scheme: {{ColorScheme(bg)}}; scrollbar-color: {{border}} transparent; scrollbar-width: thin; }
            ::-webkit-scrollbar { width: 12px; height: 12px; }
            ::-webkit-scrollbar-track { background: transparent; }
            ::-webkit-scrollbar-thumb {
                background: {{border}};
                border-radius: 8px;
                border: 3px solid {{bg}};
                background-clip: padding-box;
            }
            ::-webkit-scrollbar-thumb:hover { background: {{muted}}; background-clip: padding-box; }
            ::-webkit-scrollbar-corner { background: transparent; }
            /* pre 等パネル内の横スクロールバーはパネル背景に馴染ませる */
            pre::-webkit-scrollbar-thumb { border-color: {{panel}}; }
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

