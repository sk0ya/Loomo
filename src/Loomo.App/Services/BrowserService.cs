using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// <see cref="IBrowserService"/> 実装。AIのブラウザ操作を、人間が見ている
/// ブラウザペインの**アクティブタブ**へ一本化する（<see cref="TerminalService"/> がコマンドを
/// 可視ターミナルへ一本化するのと同じ思想）。ShellWindow がタブの生成・切替・破棄のたびに
/// <see cref="SetActiveView"/> で現在の WebView2 を結びつける。
///
/// WebView2 は WPF コントロールなので、全操作を UI スレッド上で実行する。
/// </summary>
public sealed class BrowserService : IBrowserService
{
    /// <summary>ナビゲーション完了待ちの上限。これを超えたら（ハング回避のため）現在状態で返す。</summary>
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);

    private WebView2CompositionControl? _view;

    /// <summary>操作対象のアクティブな WebView2 を結びつける（UI スレッドから呼ぶ）。</summary>
    public void SetActiveView(WebView2CompositionControl? view) => _view = view;

    // WebView2 はスレッドアフィニティを持つため、null 判定であっても UI スレッドで読む
    // （ツール実行は UI スレッドとは限らない）。
    public bool IsAvailable
    {
        get
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return false;
            return dispatcher.CheckAccess()
                ? _view?.CoreWebView2 is not null
                : dispatcher.Invoke(() => _view?.CoreWebView2 is not null);
        }
    }

    public Task<BrowserPageInfo> NavigateAsync(string url, CancellationToken ct)
        => OnUiAsync(async core =>
        {
            var target = NormalizeUrl(url);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 我々が開始したナビゲーションの完了だけを待つ。購読～Navigate は UI スレッド上で
            // 同期に行うため、Navigate 後に最初へ届く NavigationStarting が我々のものになる。
            // 進行中だった別ナビゲーションの完了で誤って確定するのを防ぐ。
            ulong? expectedId = null;
            void OnStarting(object? s, CoreWebView2NavigationStartingEventArgs e) => expectedId ??= e.NavigationId;
            void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                if (expectedId is null || e.NavigationId == expectedId)
                    tcs.TrySetResult(e.IsSuccess);
            }

            core.NavigationStarting += OnStarting;
            core.NavigationCompleted += OnCompleted;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(NavigationTimeout);
            try
            {
                core.Navigate(target);
                using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
                    await tcs.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 完了通知が来ないままタイムアウト：ハングを避け、現在の状態で返す。
            }
            finally
            {
                core.NavigationStarting -= OnStarting;
                core.NavigationCompleted -= OnCompleted;
            }

            return new BrowserPageInfo(core.Source, core.DocumentTitle);
        }, ct);

    public Task<BrowserPageInfo> GetPageInfoAsync(CancellationToken ct)
        => OnUiAsync(core => Task.FromResult(new BrowserPageInfo(core.Source, core.DocumentTitle)), ct);

    public Task<string> GetVisibleTextAsync(CancellationToken ct)
        => OnUiAsync(async core =>
        {
            var json = await core.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
            return DecodeScriptString(json);
        }, ct);

    public Task ClickAsync(string selector, CancellationToken ct)
        => OnUiAsync(async core =>
        {
            var script =
                $"(function(){{var el=document.querySelector({JsonSerializer.Serialize(selector)});" +
                "if(!el)return false;el.scrollIntoView({block:'center'});el.click();return true;})()";
            if (await core.ExecuteScriptAsync(script) != "true")
                throw new InvalidOperationException($"セレクタに一致する要素が見つかりません: {selector}");
            return true;
        }, ct);

    public Task TypeAsync(string selector, string text, CancellationToken ct)
        => OnUiAsync(async core =>
        {
            // value を持つ要素（input/textarea/select 等）と contenteditable を区別し、
            // どちらでもない要素には value を代入せず明確に失敗させる（成功を偽装しない）。
            var script =
                $"(function(){{var el=document.querySelector({JsonSerializer.Serialize(selector)});" +
                "if(!el)return 'notfound';el.focus();" +
                $"var v={JsonSerializer.Serialize(text)};" +
                "if('value' in el){el.value=v;el.dispatchEvent(new Event('input',{bubbles:true}));" +
                "el.dispatchEvent(new Event('change',{bubbles:true}));return 'ok';}" +
                "if(el.isContentEditable){el.textContent=v;" +
                "el.dispatchEvent(new Event('input',{bubbles:true}));return 'ok';}" +
                "return 'notinput';})()";
            var result = DecodeScriptString(await core.ExecuteScriptAsync(script));
            if (result == "notfound")
                throw new InvalidOperationException($"セレクタに一致する要素が見つかりません: {selector}");
            if (result != "ok")
                throw new InvalidOperationException($"指定要素はテキスト入力できません: {selector}");
            return true;
        }, ct);

    public Task<byte[]> CaptureScreenshotAsync(CancellationToken ct)
        => OnUiAsync(async core =>
        {
            using var ms = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return ms.ToArray();
        }, ct);

    /// <summary>UIスレッドでアクティブな CoreWebView2 に対し処理を実行する。未アタッチなら例外。</summary>
    private Task<T> OnUiAsync<T>(Func<CoreWebView2, Task<T>> action, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("UI ディスパッチャが利用できません。");

        return dispatcher.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var core = _view?.CoreWebView2
                ?? throw new InvalidOperationException("ブラウザペインにアクティブなタブがありません。");
            return action(core);
        }).Task.Unwrap();
    }

    /// <summary>ExecuteScriptAsync は結果を JSON で返すため、文字列リテラルへデコードする。</summary>
    private static string DecodeScriptString(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "null") return string.Empty;
        try
        {
            return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>スキーム省略時は https を補う。</summary>
    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        return url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
    }
}
