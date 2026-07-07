using System;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

/// <summary>Markdown プレビューのフル HTML 文書の組み立て（テーマ別 CSS とスクロール同期／mermaid の JS を含む）。
/// Markdown 本体のパースは <see cref="MarkdownRenderer"/>。</summary>
internal static class MarkdownPage
{
    internal static string BuildPage(
        string body, string? title, string styleName, string? baseHref = null,
        PreviewMode mode = PreviewMode.Document, string? marpMarkdown = null, bool presentation = false)
    {
        var t = title != null ? MarkdownRenderer.Encode(title) : "Preview";
        var css = PreviewCss(styleName);
        var baseTag = string.IsNullOrEmpty(baseHref) ? "" : $"<base href=\"{MarkdownRenderer.EncodeAttribute(baseHref)}\">";
        var mermaidTheme = NormalizeStyle(styleName) is "Light" or "GitHub" ? "default" : "dark";

        // 描画モード（ページ側 JS が読む）。marp は本文を空のステージにし、生 Markdown を JS へ渡す。
        var modeJs = mode == PreviewMode.Marp ? "marp" : "document";
        // presentation=発表（1枚ずつ・キー送り）／既定は縦並びで全スライド表示（スクロール）。
        var presentationJs = presentation ? "true" : "false";
        var bodyClass = presentation ? " class=\"loomo-present\""
                      : mode == PreviewMode.Marp ? " class=\"loomo-vertical\""
                      : "";
        var pageBody = mode == PreviewMode.Marp ? "<div id=\"marp-root\"></div>" : body;
        // 生 Markdown は <script> 内 JS 文字列として埋め込む。既定エンコーダが <,>,& を \uXXXX 化するので
        // </script> でタグが閉じる事故が起きない。
        var marpBootstrap = mode == PreviewMode.Marp && marpMarkdown is not null
            ? $"<script>window.__marpSrc = {System.Text.Json.JsonSerializer.Serialize(marpMarkdown)};</script>"
            : "";

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

                // 描画モード（C# が決めてページに焼き込む）。marp はスライド、document は従来表示。
                const previewMode = '{{modeJs}}';
                // presentation=発表（1枚ずつ・キー送り）。false＝縦並びで全スライド表示（スクロール）が既定。
                const presentation = {{presentationJs}};
                const isMarp = previewMode === 'marp';
                const marpSrc = 'https://{{MarkdownRenderer.AssetsVirtualHost}}/marp.min.js';

                let slides = [];
                let slideIndex = 0;
                let indicatorEl = null;

                // marp-core（3.6MB）はスライドが marp のときだけ遅延ロードする。
                let marpLoading = false, marpReady = false, pendingMarp = null, marpInstance = null;
                function ensureMarp(cb) {
                    if (marpReady) { cb(); return; }
                    pendingMarp = cb;
                    if (marpLoading) return;
                    marpLoading = true;
                    const s = document.createElement('script');
                    s.src = marpSrc;
                    s.onload = () => { marpReady = true; const c = pendingMarp; pendingMarp = null; if (c) c(); };
                    s.onerror = () => { marpLoading = false; };
                    document.head.appendChild(s);
                }

                function renderMarp(markdown) {
                    ensureMarp(() => {
                        try {
                            marpInstance = marpInstance || new window.Marp({ inlineSVG: false, html: true });
                            const out = marpInstance.render(markdown);
                            let style = document.getElementById('marp-style');
                            if (!style) { style = document.createElement('style'); style.id = 'marp-style'; document.head.appendChild(style); }
                            style.textContent = out.css;
                            const root = document.getElementById('marp-root');
                            if (!root) return;
                            root.innerHTML = out.html;
                            layoutAfterBuild(root);
                        } catch (e) {}
                    });
                }

                function collectSlides(scope) {
                    slides = Array.prototype.slice.call((scope || document).querySelectorAll('section'));
                    if (slideIndex >= slides.length) slideIndex = Math.max(0, slides.length - 1);
                }

                // スライド群を組み立てた後の配置。発表＝1枚ずつ、既定＝縦並び全表示。
                function layoutAfterBuild(scope) {
                    collectSlides(scope);
                    if (presentation) showSlide(slideIndex);
                    else layoutVertical();
                    renderMermaid();
                }

                // 縦並び（既定）：全スライドを表示したまま、横幅に合わせて zoom で縮小する（高さも追従＝積み重ね）。
                function verticalZoomTarget() {
                    return document.querySelector('#marp-root .marpit');
                }
                function applyVerticalZoom() {
                    const c = verticalZoomTarget();
                    if (!c) return;
                    c.style.zoom = Math.min(1, window.innerWidth / 1312);   // 1280 + 余白
                }
                function layoutVertical() {
                    for (let k = 0; k < slides.length; k++) {
                        const s = slides[k];
                        s.style.display = '';
                        s.style.position = '';
                        s.style.left = s.style.top = s.style.transform = '';
                    }
                    applyVerticalZoom();
                }

                // 発表（1枚ずつ）：固定サイズのスライドを画面中央へ拡大表示する。
                function layoutSlide() {
                    const s = slides[slideIndex];
                    if (!s) return;
                    const sw = s.offsetWidth || 1280, sh = s.offsetHeight || 720;
                    const scale = Math.min(window.innerWidth / sw, window.innerHeight / sh);
                    s.style.position = 'absolute';
                    s.style.left = '50%';
                    s.style.top = '50%';
                    s.style.margin = '0';
                    s.style.transformOrigin = 'center center';
                    s.style.transform = 'translate(-50%, -50%) scale(' + scale + ')';
                }

                function showSlide(i) {
                    if (!slides.length) { updateIndicator(); return; }
                    slideIndex = Math.min(Math.max(0, i), slides.length - 1);
                    for (let k = 0; k < slides.length; k++)
                        slides[k].style.display = k === slideIndex ? 'block' : 'none';
                    layoutSlide();
                    updateIndicator();
                }

                function updateIndicator() {
                    if (!presentation) return;
                    if (!indicatorEl) {
                        indicatorEl = document.createElement('div');
                        indicatorEl.className = 'loomo-slide-indicator';
                        document.body.appendChild(indicatorEl);
                    }
                    indicatorEl.textContent = slides.length ? (slideIndex + 1) + ' / ' + slides.length : '';
                    indicatorEl.style.display = slides.length ? 'block' : 'none';
                }

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
                    // 差し替え前の要素は detach 済み。開いたままだと overflow ロックが残るので解除して作り直す。
                    lightboxEl = null;
                    lightboxImg = null;
                    document.documentElement.style.overflow = '';
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
                    if (presentation) return;   // 発表中は縦スクロール同期しない（ページ送りで操作）
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
                        else if (d.type === 'setBody') {
                            // marp は d.html に生 Markdown が載る。document は本文 HTML を差し替える。
                            if (previewMode === 'marp') renderMarp(d.html);
                            else applyBody(d.html);
                        }
                    });
                }

                // 初期化（モード別）。marp は生 Markdown を描画、document は従来どおり mermaid を描く。
                // スクリプトは head で走るので body 解析後に呼ぶ。
                function initPreview() {
                    if (previewMode === 'marp') {
                        if (typeof window.__marpSrc === 'string') renderMarp(window.__marpSrc);
                        else updateIndicator();
                    } else {
                        renderMermaid();
                    }
                }
                if (document.readyState === 'loading')
                    document.addEventListener('DOMContentLoaded', initPreview);
                else
                    initPreview();

                // 発表モードのページ送り（キーボード）。f で全画面。
                if (presentation) {
                    window.addEventListener('keydown', e => {
                        if (e.key === 'ArrowRight' || e.key === 'ArrowDown' || e.key === 'PageDown' || e.key === ' ') {
                            showSlide(slideIndex + 1); e.preventDefault();
                        } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp' || e.key === 'PageUp') {
                            showSlide(slideIndex - 1); e.preventDefault();
                        } else if (e.key === 'Home') {
                            showSlide(0); e.preventDefault();
                        } else if (e.key === 'End') {
                            showSlide(slides.length - 1); e.preventDefault();
                        } else if (e.key === 'f' || e.key === 'F') {
                            if (document.fullscreenElement) document.exitFullscreen();
                            else document.documentElement.requestFullscreen();
                            e.preventDefault();
                        }
                    });
                }

                // preview→host(editor) も 1 フレーム 1 回へ間引いてメッセージの氾濫を防ぐ。
                window.addEventListener('scroll', () => {
                    if (presentation || suppressScrollMessage || reportScheduled || !window.chrome?.webview) return;
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

                // --- コードブロックのコピーボタン ---
                // コードは <pre><code> の textContent から取る（表示中の HTML エンティティは
                // ブラウザが復元してくれるので、C# 側に生テキストを二重に埋め込む必要が無い）。
                document.addEventListener('click', e => {
                    const btn = e.target.closest && e.target.closest('.code-copy-btn');
                    if (!btn) return;
                    const codeEl = btn.parentElement && btn.parentElement.querySelector('pre > code');
                    if (!codeEl) return;
                    copyText(codeEl.textContent);
                    btn.classList.add('copied');
                    setTimeout(() => btn.classList.remove('copied'), 700);
                });

                // --- 画像クリックで拡大表示（ライトボックス）。marp スライドでは行わない。 ---
                // 開いている間は documentElement を overflow:hidden にして背景（プレビュー本体）の
                // スクロールを止め、ホイールは拡大縮小、ドラッグは表示位置の移動に使う。
                let lightboxEl = null, lightboxImg = null;
                let lbScale = 1, lbTx = 0, lbTy = 0;
                let lbDragging = false, lbDragStartX = 0, lbDragStartY = 0, lbStartTx = 0, lbStartTy = 0;

                function applyLightboxTransform() {
                    if (lightboxImg) lightboxImg.style.transform = 'translate(' + lbTx + 'px,' + lbTy + 'px) scale(' + lbScale + ')';
                }
                function resetLightboxTransform() {
                    lbScale = 1; lbTx = 0; lbTy = 0;
                    applyLightboxTransform();
                }
                function closeLightbox() {
                    if (!lightboxEl) return;
                    lightboxEl.classList.remove('open');
                    document.documentElement.style.overflow = '';
                }
                function ensureLightbox() {
                    if (lightboxEl) return;
                    lightboxEl = document.createElement('div');
                    lightboxEl.className = 'loomo-lightbox';
                    // 背景（自分自身）のクリックだけ閉じる。画像上のクリックはドラッグ操作に使うため閉じない。
                    lightboxEl.addEventListener('click', ev => {
                        if (ev.target === lightboxEl) closeLightbox();
                    });
                    // ホイールは背景スクロールへ流さず拡大縮小に使う。
                    lightboxEl.addEventListener('wheel', ev => {
                        ev.preventDefault();
                        const factor = ev.deltaY < 0 ? 1.15 : 1 / 1.15;
                        lbScale = Math.min(8, Math.max(0.2, lbScale * factor));
                        applyLightboxTransform();
                    }, { passive: false });
                    lightboxImg = document.createElement('img');
                    lightboxImg.addEventListener('mousedown', ev => {
                        ev.preventDefault();
                        lbDragging = true;
                        lbDragStartX = ev.clientX; lbDragStartY = ev.clientY;
                        lbStartTx = lbTx; lbStartTy = lbTy;
                        lightboxImg.style.cursor = 'grabbing';
                    });
                    lightboxImg.addEventListener('dblclick', () => resetLightboxTransform());
                    window.addEventListener('mousemove', ev => {
                        if (!lbDragging) return;
                        lbTx = lbStartTx + (ev.clientX - lbDragStartX);
                        lbTy = lbStartTy + (ev.clientY - lbDragStartY);
                        applyLightboxTransform();
                    });
                    window.addEventListener('mouseup', () => {
                        if (!lbDragging) return;
                        lbDragging = false;
                        if (lightboxImg) lightboxImg.style.cursor = 'grab';
                    });
                    lightboxEl.appendChild(lightboxImg);
                    document.body.appendChild(lightboxEl);
                }
                document.addEventListener('click', e => {
                    if (isMarp) return;
                    const img = e.target.closest && e.target.closest('img');
                    if (!img || (img.closest && img.closest('a'))) return;
                    if (img === lightboxImg) return;  // ライトボックス内の画像自体のクリック（ドラッグ後の click 含む）は無視
                    ensureLightbox();
                    lightboxImg.src = img.currentSrc || img.src;
                    lightboxImg.alt = img.alt || '';
                    resetLightboxTransform();
                    lightboxImg.style.cursor = 'grab';
                    lightboxEl.classList.add('open');
                    document.documentElement.style.overflow = 'hidden';
                });
                window.addEventListener('keydown', e => {
                    if (e.key === 'Escape') closeLightbox();
                });

                // GFM タスクリストのチェックボックスはプレビュー上で操作可能（disabled にしていない）。
                // トグルしたら対応するソース行（data-line）をホストへ伝え、エディタ側の実ファイルを書き換える。
                // 新しいチェック状態はソース側で計算し直す（ここでは行番号だけ伝える＝表示と実体が食い違わない）。
                document.addEventListener('change', e => {
                    const cb = e.target.closest && e.target.closest('.task-list-item input[type="checkbox"]');
                    if (!cb || !window.chrome?.webview) return;
                    const line = Number(cb.getAttribute('data-line'));
                    if (Number.isFinite(line))
                        window.chrome.webview.postMessage({ type: 'toggleTaskCheckbox', line });
                });

                // 同一ページ内アンカー（#見出し 等）はブラウザの既定ナビゲーションに任せず自前でスクロールする。
                // <base href> が本文の相対画像解決のため別オリジン（preview.loomo）を指しているので、
                // href="#id" を既定動作に委ねると base 側の URL への遷移になり、そこには何も配信されて
                // おらず到達不能ページに飛んでしまう（フラグメントは常に「今の文書」宛のはずが base 起点で
                // 解決されてしまう）。location.hash の代入は base の影響を受けないため安全に使える。
                function scrollToFragment(href) {
                    const id = decodeURIComponent(href.slice(1));
                    if (!id) return;
                    const target = document.getElementById(id) || document.getElementsByName(id)[0];
                    if (target) target.scrollIntoView();
                    try { history.replaceState(null, '', '#' + id); } catch (e) {}
                }

                // 本文中のリンク（<a href>）クリックはページ内遷移させず、ホスト（Loomo）へ振り分ける。
                // 同一ページ内アンカー（#見出し 等）だけは上の自前スクロールに任せる。見出しのパーマリンク
                // （.heading-anchor）はさらに「#見出しid」をクリップボードへコピーする。
                document.addEventListener('click', e => {
                    const anchor = e.target.closest && e.target.closest('a.heading-anchor');
                    if (anchor) {
                        const href = anchor.getAttribute('href') || '';
                        if (href.startsWith('#')) {
                            e.preventDefault();
                            copyText(href);
                            scrollToFragment(href);
                            anchor.classList.add('copied');
                            setTimeout(() => anchor.classList.remove('copied'), 700);
                        }
                        return;
                    }
                    const a = e.target.closest && e.target.closest('a[href]');
                    if (!a) return;
                    const href = a.getAttribute('href');
                    if (!href) return;
                    if (href.startsWith('#')) {
                        e.preventDefault();
                        scrollToFragment(href);
                        return;
                    }
                    if (!window.chrome?.webview) return;
                    e.preventDefault();
                    window.chrome.webview.postMessage({ type: 'linkClicked', href });
                });

                // 画面サイズ・ペイン幅・表示切替で innerHeight/scrollHeight が変わると、絶対 scrollY を
                // 保つブラウザの挙動でエディタとの比率がズレる。resize 中は scroll エコーを止めて
                // （リフロー起因の scroll でエディタが飛ぶのを防ぐ）、最後に意図した比率へ貼り直す。
                window.addEventListener('resize', () => {
                    if (presentation) { layoutSlide(); return; }   // 発表：現在の1枚を再フィット
                    if (isMarp) { applyVerticalZoom(); return; }   // marp 縦並び：横幅に合わせ zoom 再計算
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
            <body{{bodyClass}}>{{marpBootstrap}}{{pageBody}}</body>
            </html>
            """;
    }

    public static string NormalizeStyle(string? styleName) =>
        styleName?.Trim().ToLowerInvariant() switch
        {
            "dark" => "Dark",
            "light" => "Light",
            "github" => "GitHub",
            "nord" => "Nord",
            "tokyonight" => "TokyoNight",
            "onedark" => "OneDark",
            "solarizeddark" => "SolarizedDark",
            "monokai" => "Monokai",
            _ => "Dracula",
        };

    private static string PreviewCss(string styleName) => NormalizeStyle(styleName) switch
    {
        "Light" => BaseCss("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#0969DA", "#8250DF", "#953800", "#57606A", "#116329"),
        "GitHub" => BaseCss("#FFFFFF", "#24292F", "#F6F8FA", "#D0D7DE", "#0969DA", "#24292F", "#CF222E", "#57606A", "#0550AE"),
        "Dark" => BaseCss("#1E1E1E", "#D4D4D4", "#252526", "#3C3C3C", "#4FC1FF", "#DCDCAA", "#CE9178", "#9CDCFE", "#B5CEA8"),
        "Nord" => BaseCss("#2E3440", "#D8DEE9", "#3B4252", "#4C566A", "#88C0D0", "#81A1C1", "#D08770", "#616E88", "#A3BE8C"),
        "TokyoNight" => BaseCss("#1A1B26", "#C0CAF5", "#24283B", "#414868", "#7AA2F7", "#BB9AF7", "#E0AF68", "#565F89", "#9ECE6A"),
        "OneDark" => BaseCss("#282C34", "#ABB2BF", "#21252B", "#3E4451", "#61AFEF", "#C678DD", "#E5C07B", "#5C6370", "#98C379"),
        "SolarizedDark" => BaseCss("#002B36", "#93A1A1", "#073642", "#586E75", "#268BD2", "#2AA198", "#CB4B16", "#586E75", "#859900"),
        "Monokai" => BaseCss("#272822", "#F8F8F2", "#3E3D32", "#49483E", "#66D9EF", "#F92672", "#FD971F", "#75715E", "#A6E22E"),
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
            img { max-width: 100%; border-radius: 4px; display: block; margin: 8px 0; cursor: zoom-in; }
            hr { border: none; border-top: 1px solid {{border}}; margin: 20px 0; }

            /* GFM タスクリスト（- [ ] / - [x]）：箇条書きマーカーを消してチェックボックスに差し替える */
            li.task-list-item { list-style: none; margin-left: -20px; }
            li.task-list-item input[type="checkbox"] { margin-right: 6px; vertical-align: middle; cursor: pointer; }

            /* 見出しのパーマリンク（ホバーで # が出て、クリックでアンカーをコピー） */
            h1, h2, h3, h4, h5, h6 { position: relative; }
            .heading-anchor {
                position: absolute; left: -1.1em; color: {{muted}}; opacity: 0;
                text-decoration: none; font-weight: 400; transition: opacity .1s;
            }
            h1:hover .heading-anchor, h2:hover .heading-anchor, h3:hover .heading-anchor,
            h4:hover .heading-anchor, h5:hover .heading-anchor, h6:hover .heading-anchor { opacity: 1; }
            .heading-anchor.copied { opacity: 1; color: {{link}}; }

            /* コードブロックのコピーボタン（ホバーで表示） */
            .code-block { position: relative; }
            .code-block pre { margin: 14px 0; }
            .code-copy-btn {
                position: absolute; top: 8px; right: 8px; opacity: 0;
                background: {{bg}}; color: {{muted}}; border: 1px solid {{border}};
                border-radius: 4px; padding: 2px 7px; font-size: 12px; cursor: pointer;
                transition: opacity .1s;
            }
            .code-block:hover .code-copy-btn { opacity: .85; }
            .code-copy-btn:hover { color: {{fg}}; }
            .code-copy-btn.copied { opacity: 1; color: {{link}}; border-color: {{link}}; }

            /* diff / patch フェンス：行頭記号による追加・削除・ハンクの色分け */
            .diff-add { display: block; background: rgba(46, 160, 67, .15); }
            .diff-del { display: block; background: rgba(248, 81, 73, .15); }
            .diff-hunk { display: block; color: {{link}}; }
            .diff-meta { display: block; color: {{muted}}; }

            /* 脚注（[^id] / [^id]: 説明） */
            .footnotes { margin-top: 28px; font-size: .9em; color: {{muted}}; }
            .footnotes hr { margin: 0 0 12px; }
            .footnotes ol { padding-left: 20px; }
            .footnote-ref, .footnote-backref { color: {{link}}; text-decoration: none; }
            .footnote-ref { padding: 0 2px; }

            /* 目次（[[toc]] / [toc]） */
            nav.toc {
                background: {{panel}}; border: 1px solid {{border}}; border-radius: 6px;
                padding: 10px 16px; margin: 14px 0;
            }
            nav.toc ul { list-style: none; padding-left: 0; margin: 0; }
            nav.toc li { margin: 2px 0; }
            nav.toc a { color: {{fg}}; }
            nav.toc a:hover { color: {{link}}; }

            /* 画像クリックのライトボックス（拡大表示・ホイールで拡大縮小・ドラッグで移動） */
            .loomo-lightbox {
                position: fixed; inset: 0; background: rgba(0,0,0,.85);
                display: none; align-items: center; justify-content: center;
                z-index: 1000; cursor: zoom-out; padding: 24px;
                overflow: hidden; user-select: none; touch-action: none;
            }
            .loomo-lightbox.open { display: flex; }
            .loomo-lightbox img {
                max-width: 100%; max-height: 100%; margin: 0;
                border-radius: 4px; box-shadow: 0 8px 40px rgba(0,0,0,.6);
                cursor: grab; transform-origin: center center;
            }

            /* コードブロックのシンタックスハイライト（Loomo エディタと同じ字句解析器のトークン種別） */
            .tok-kw { color: {{strong}}; }
            .tok-ty { color: {{heading}}; }
            .tok-str { color: {{code}}; }
            .tok-cm { color: {{muted}}; font-style: italic; }
            .tok-num { color: {{link}}; }
            .tok-pp { color: {{strong}}; }
            .tok-at { color: {{heading}}; }
            .tok-fn { color: {{heading}}; }

            /* --- marp スライド表示（marp:true 文書のみ。非 marp は通常ドキュメント表示） --- */
            /* 既定＝縦並びで全スライドをスクロール一覧（zoom は JS が横幅に合わせて設定）。 */
            body.loomo-vertical { padding: 16px 0 40px; background: #000; }
            body.loomo-vertical #marp-root .marpit { margin: 0 auto; }
            body.loomo-vertical #marp-root section { margin: 0 auto 24px; box-shadow: 0 8px 40px rgba(0,0,0,.5); }

            /* オプション＝発表（1枚ずつ）。JS が transform で 1 枚だけ中央へ拡大する。 */
            body.loomo-present { padding: 0; overflow: hidden; height: 100vh; background: #000; }
            body.loomo-present #marp-root { position: fixed; inset: 0; overflow: hidden; }
            .loomo-slide-indicator {
                position: fixed; right: 14px; bottom: 12px; z-index: 10;
                font-size: 12px; color: {{muted}}; background: {{panel}};
                padding: 3px 10px; border-radius: 12px; opacity: .85; pointer-events: none;
            }
            """;
    }
}

