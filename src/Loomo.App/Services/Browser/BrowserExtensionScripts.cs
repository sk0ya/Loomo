namespace sk0ya.Loomo.App.Services;

/// <summary>ブラウザ拡張機能をホストするときに WebView2 ページへ注入するスクリプト資産。</summary>
internal static class BrowserExtensionScripts
{
    internal const string StoreInstallHook = """
        (() => {
          if (window.__loomoStoreHook) return;
          window.__loomoStoreHook = true;
          const labels = /(Chrome\s*に追加|Add to Chrome|Edge\s*に追加|Add to Edge|入手|^Get$)/;
          document.addEventListener('click', e => {
            const start = e.target instanceof Element ? e.target : null;
            const el = start && start.closest('button, a, [role="button"]');
            if (!el) return;
            const text = (el.innerText || el.textContent || '').trim();
            if (!labels.test(text)) return;
            e.preventDefault();
            e.stopPropagation();
            window.chrome.webview.postMessage(JSON.stringify({ loomo: 'installExtension' }));
          }, true);
        })();
        """;

    internal const string PopupMeasure = """
        (() => {
          const d = document.documentElement;
          const r = document.body ? document.body.getBoundingClientRect() : null;
          const w = r ? Math.ceil(r.right + r.left) : d.scrollWidth;
          const h = r ? Math.ceil(r.bottom + r.top) : d.scrollHeight;
          return [
            Math.max(w, d.scrollWidth > window.innerWidth ? d.scrollWidth : 0),
            Math.max(h, d.scrollHeight > window.innerHeight ? d.scrollHeight : 0),
          ];
        })()
        """;
}
