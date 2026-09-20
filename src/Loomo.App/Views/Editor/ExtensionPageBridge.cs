namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 拡張機能のページ（<c>chrome-extension://…</c>）が「別のページを開いてくれ」と言ってくる先を、
/// <b>ホスト（＝部屋のブラウザタブ）</b>へ向ける仕込み（設計書 §21.5.2）。
///
/// <para>WebView2 にはタブもツールバーも無いので、拡張機能が普段そこへ向けて呼ぶ API——
/// <c>chrome.runtime.openOptionsPage()</c>（歯車から設定画面へ）と <c>chrome.tabs.create/update</c>——は
/// <b>行き先が存在せず、押しても何も起きない</b>。ポップアップの歯車が無反応、というのがこの穴の見え方で、
/// 拡張機能側からはどうにもならない。開ける場所を持っているのはホストだけなので、こちらへ回す。</para>
///
/// <para>差し替えは<b>無条件</b>。素の実装が生きているかは中からは判別できず（何も起こさずに返るだけ）、
/// かといって両方呼べば二重に開く。WebView2 では「開く先はホストのタブ」以外に正解が無いので、
/// 迷わずこちらに寄せる。効かせるのは <c>chrome-extension:</c> のページだけ——普通のページの
/// <c>window.chrome</c> には触らない。</para>
/// </summary>
internal static class ExtensionPageBridge
{
    /// <summary>ページから届く合図の種類（<see cref="TryReadOpenRequest"/> が読む）。</summary>
    public const string OpenPageKind = "openExtensionPage";

    /// <summary>ドキュメント生成時に流し込むページ側スクリプト
    /// （<c>AddScriptToExecuteOnDocumentCreatedAsync</c> で登録する）。
    ///
    /// <para>設定画面の場所は<b>ページ自身に訊く</b>（<c>runtime.getManifest()</c>）——ホストが埋め込むと
    /// 拡張機能ごとにスクリプトを作り分けることになるうえ、ポップアップとタブで別々の値を持つ羽目になる。
    /// ホストへ渡せなかったときだけ、その場（ポップアップの中）で設定画面へ移る——狭くても、
    /// 押して何も起きないよりはよい。</para>
    ///
    /// <para><b>Promise を返す</b>のを忘れない。MV3 のこれらの API はコールバックを渡さなければ Promise を返し、
    /// <c>chrome.tabs.create({…}).then(…)</c> と書くのが今風の作法なので、<c>undefined</c> を返すと
    /// ページの中で <c>TypeError</c> になる——歯車が無反応、というこの仕込みが直したはずの症状に、
    /// 別の拡張機能で戻ってしまう。</para></summary>
    public const string Script = """
        (() => {
          if (location.protocol !== 'chrome-extension:') return;
          const send = url => {
            if (!url) return false;
            try {
              window.chrome.webview.postMessage(JSON.stringify({
                loomo: 'openExtensionPage', url: String(new URL(url, location.href)),
              }));
              return true;
            } catch (e) { return false; }
          };
          const api = window.chrome;
          if (!api) return;
          const runtime = api.runtime;
          if (runtime) {
            try {
              runtime.openOptionsPage = callback => {
                const m = typeof runtime.getManifest === 'function' ? runtime.getManifest() : null;
                const page = m && ((m.options_ui && m.options_ui.page) || m.options_page);
                const url = page && typeof runtime.getURL === 'function' ? runtime.getURL(page) : null;
                if (url && !send(url)) location.href = url;
                if (typeof callback === 'function') callback();
                return Promise.resolve();
              };
            } catch (e) { /* 差し替えを拒む造りなら、素の（何も起きない）ままにする */ }
          }
          try {
            const tabs = api.tabs || (api.tabs = {});
            tabs.create = (props, callback) => {
              send(props && props.url);
              if (typeof callback === 'function') callback({});
              return Promise.resolve({});
            };
            // update(props, cb) / update(tabId, props, cb) の両方の呼ばれ方がある。
            tabs.update = (first, second, third) => {
              const props = first && typeof first === 'object' ? first : second;
              const callback = [third, second].find(a => typeof a === 'function');
              send(props && props.url);
              if (typeof callback === 'function') callback({});
              return Promise.resolve({});
            };
          } catch (e) { /* 同上 */ }
        })();
        """;

    /// <summary>ページから届いた <c>webMessage</c> が「このページを開いてくれ」なら、その URL を返す。
    ///
    /// <para>受けるのは<b>拡張機能のページからのものだけ</b>（<paramref name="source"/> を見る）。
    /// この合図は誰でも送れるので、普通のページから <c>chrome-extension://…</c> を開かせる口にしない。
    /// 行き先も <c>http(s)</c> と <c>chrome-extension</c> に絞る（<c>javascript:</c> や <c>file:</c> は通さない）。</para></summary>
    public static bool TryReadOpenRequest(string? webMessageAsJson, string? source, out string url)
    {
        url = "";
        if (!IsExtensionPage(source))
            return false;
        try
        {
            var outer = JsonSerializer.Deserialize<JsonElement>(webMessageAsJson ?? "");
            // postMessage(string) は JSON 文字列として届くので、二重に解く。
            var payload = outer.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<JsonElement>(outer.GetString()!)
                : outer;
            // 型まで見てから読む（ページは何でも送れる）。
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("loomo", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != OpenPageKind
                || !payload.TryGetProperty("url", out var target)
                || target.ValueKind != JsonValueKind.String)
                return false;
            var candidate = target.GetString() ?? "";
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsOpenableScheme(uri.Scheme))
                return false;
            url = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;   // ページが送る他の web message は素通しする
        }
    }

    public static bool IsExtensionPage(string? url)
        => url is not null && url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenableScheme(string scheme)
        => scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
           || scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
           || scheme.Equals("chrome-extension", StringComparison.OrdinalIgnoreCase);
}
