using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace sk0ya.Loomo.App.Services;

/// <summary>EditorSupport の検索ハイライト用ページスクリプトとメッセージ形式。</summary>
internal static class EditorSupportSearchHighlightBridge
{
    public const string MessageType = "setSearchHighlight";
    public const string Script = """
        (() => {
            const NAME = 'loomo-search';
            const MAX = 5000;               // 巨大な文書で塗り過ぎないための上限
            let term = '', caseSensitive = false, useRegex = false, timer = 0, observer = null;

            function styleOnce() {
                if (document.getElementById('loomo-search-style')) return;
                const head = document.head || document.documentElement;
                if (!head) return;
                const style = document.createElement('style');
                style.id = 'loomo-search-style';
                // アプリのテーマの SearchHighlight（半透明の琥珀）に合わせる。文字色は触らない
                // ＝プレビューのテーマが明るくても暗くても読めるようにする。
                style.textContent = '::highlight(' + NAME + '){background-color:rgba(240,190,70,0.5);}';
                head.appendChild(style);
            }

            function build() {
                if (!window.CSS || !CSS.highlights || typeof Highlight !== 'function') return;
                CSS.highlights.delete(NAME);
                if (!term || !document.body) return;
                let re;
                try {
                    const source = useRegex ? term : term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
                    re = new RegExp(source, caseSensitive ? 'g' : 'gi');
                } catch {
                    return;                 // 入力途中の不正な正規表現は塗らない
                }
                styleOnce();
                const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
                    acceptNode(node) {
                        const parent = node.parentElement;
                        if (!parent) return NodeFilter.FILTER_REJECT;
                        const tag = parent.tagName;
                        if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'TEXTAREA')
                            return NodeFilter.FILTER_REJECT;
                        return node.nodeValue ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT;
                    }
                });
                const ranges = [];
                for (let node = walker.nextNode(); node && ranges.length < MAX; node = walker.nextNode()) {
                    const text = node.nodeValue;
                    re.lastIndex = 0;
                    for (let m = re.exec(text); m; m = re.exec(text)) {
                        if (m[0].length === 0) { re.lastIndex++; continue; }   // ゼロ幅一致で止まらないように
                        const range = document.createRange();
                        range.setStart(node, m.index);
                        range.setEnd(node, m.index + m[0].length);
                        ranges.push(range);
                        if (ranges.length >= MAX) break;
                    }
                }
                if (ranges.length) CSS.highlights.set(NAME, new Highlight(...ranges));
            }

            function schedule() {
                clearTimeout(timer);
                timer = setTimeout(build, 40);
            }

            function observe() {
                if (observer || !document.body) return;
                // 本文差し替え（setBody）・mermaid の遅延描画で塗り直す。
                observer = new MutationObserver(() => { if (term) schedule(); });
                observer.observe(document.body, { childList: true, subtree: true, characterData: true });
            }

            function start() { observe(); schedule(); }
            if (document.readyState === 'loading') addEventListener('DOMContentLoaded', start);
            else start();

            window.chrome?.webview?.addEventListener('message', e => {
                const d = e.data;
                if (!d || d.type !== 'setSearchHighlight') return;
                term = d.query || '';
                caseSensitive = !!d.caseSensitive;
                useRegex = !!d.useRegex;
                start();
            });
        })();
        """;

    public static string SerializeMessage(string term, bool caseSensitive, bool useRegex)
        => JsonSerializer.Serialize(new
        {
            type = MessageType,
            query = term,
            caseSensitive,
            useRegex,
        });

    public static void Post(CoreWebView2 core, string term, bool caseSensitive, bool useRegex)
    {
        try { core.PostWebMessageAsJson(SerializeMessage(term, caseSensitive, useRegex)); }
        catch { /* WebView の破棄と競合した場合は次の描画で再送する */ }
    }
}
