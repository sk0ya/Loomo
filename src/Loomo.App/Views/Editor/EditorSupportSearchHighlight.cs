namespace sk0ya.Loomo.App.Views;

/// <summary>
/// EditorSupport ペイン（Markdown プレビュー等の WebView2 表示）で、検索パネルの検索ワードに
/// 一致する箇所を塗るための仕込み。プレビューは検索結果一覧には出てこないので、エディタ側だけを
/// 塗ると「ヒットしたのにプレビューのどこか分からない」状態になる——それを埋める。
/// <para>
/// 塗りは <b>CSS Custom Highlight API</b>（<c>CSS.highlights</c> ＋ <c>Range</c>）で行い、DOM は
/// 一切書き換えない。<c>&lt;mark&gt;</c> で包む方式だと本文差し替え（setBody）・スクロール同期・
/// リンククリック・タスクチェックボックスの行番号がずれるため。
/// </para>
/// <para>
/// 一致はテキストノード単位なので、インライン要素をまたぐ語（Markdown の <c>**強調**</c> の途中など）は
/// 塗られない（既知の制限）。
/// </para>
/// </summary>
internal static class EditorSupportSearchHighlight
{
    /// <summary>ホスト → ページのメッセージ種別。</summary>
    public const string MessageType = "setSearchHighlight";

    /// <summary>現在のハイライト条件をページへ送る（ワードが空なら消える）。</summary>
    public static void Post(CoreWebView2 core, string term, bool caseSensitive, bool useRegex)
    {
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = MessageType,
                query = term,
                caseSensitive,
                useRegex,
            }));
        }
        catch { /* ページ未ロード等：次の描画で送り直される */ }
    }

    /// <summary>
    /// ドキュメント生成時に流し込むページ側スクリプト
    /// （<c>AddScriptToExecuteOnDocumentCreatedAsync</c> で登録する）。ホストからの
    /// <see cref="MessageType"/> メッセージを受けて塗り直し、本文が差し替わったら MutationObserver で
    /// 追従する。塗るのは <c>CSS.highlights</c> だけなので、監視が自分の変更で再発火することはない。
    /// </summary>
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
}
