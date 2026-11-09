namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ブラウザペインのコマンド（<c>browser.*</c>）とマウスジェスチャー（§24.105）。
/// ジェスチャーはキーと同じコマンド Id を呼ぶだけ——実体はここ（とキーボードの id→アクション表）に 1 つ。</summary>
public partial class ShellWindow {
    /// <summary>ジェスチャーの仕込みを入れる。<b>最初の遷移より先に</b>（ドキュメント生成時の仕込みなので）。
    /// 切っているときは入れない——入れておくと、右ドラッグの後のメニューを黙って止めてしまう。</summary>
    private async Task InstallBrowserGestureScriptAsync(CoreWebView2 core) {
        if (!_settings.MouseGestures.Enabled)
            return;
        try {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BrowserMouseGesture.Script);
        } catch {
            // 仕込めなくてもページは普通に見られる。
        }
    }

    /// <summary>ページから届いたジェスチャーの合図なら実行して true。</summary>
    private bool TryHandleBrowserGesture(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs e) {
        if (!BrowserMouseGesture.TryRead(e.WebMessageAsJson, BrowserMouseGesture.Token, out var gesture))
            return false;
        // 描いたタブを前提にしたコマンド（コピー・戻る・閉じる）なので、見ていないタブからの合図は捨てる。
        if (!_settings.MouseGestures.Enabled || !ReferenceEquals(_activeBrowserTab, tab))
            return true;
        if (BrowserMouseGesture.Resolve(_settings.MouseGestures.Bindings, gesture) is not { } id) {
            ToastService.Info($"ジェスチャー {BrowserMouseGesture.Arrows(gesture)} には何も割り当てられていません。");
            return true;
        }
        if (_keyboard?.TryExecute(id) != true)
            ToastService.Warning($"ジェスチャー {BrowserMouseGesture.Arrows(gesture)} のコマンド「{id}」は見つかりません。");
        return true;
    }

    /// <summary>表示中のページを <c>[題](URL)</c>（テキスト）と <c>&lt;a&gt;</c>（HTML）の 2 形で写す。
    /// Teams・Outlook へ貼ればリンク、エディタへ貼れば Markdown（§24.46 と同じ組み立て）。</summary>
    private void CopyActiveBrowserLink() {
        var tab = _activeBrowserTab;
        var url = BrowserUrlOf(tab);
        var title = tab?.View.TryCore()?.DocumentTitle is { Length: > 0 } documentTitle ? documentTitle : tab?.Title;
        if (BrowserTabLink.Markdown(title, url) is not { } markdown) {
            ToastService.Info("このページのリンクは写せません。");
            return;
        }
        ClipboardText.SetLink(markdown, BrowserTabLink.Html(title, url)!);
        ToastService.Success($"リンクをコピーしました：{markdown}");
    }

    private void CopyActiveBrowserUrl() {
        if (BrowserTabLink.Url(BrowserUrlOf(_activeBrowserTab)) is not { } url) {
            ToastService.Info("このページの URL は写せません。");
            return;
        }
        ClipboardText.Set(url);
        ToastService.Success($"URL をコピーしました：{url}");
    }

    /// <summary>設定から足したスクリプトのコマンド（§24.106）の実体。Id が無ければ null（キーの実行表が訊いてくる）。
    /// 走らせるときに設定を引き直すので、スクリプトを書き換えたら次の 1 回から効く。</summary>
    private Action? BrowserScriptCommandAction(string id)
        => id.StartsWith(BrowserScriptCommand.IdPrefix, StringComparison.Ordinal)
           && _settings.BrowserScripts.Any(c => c.Id == id)
            ? () => _ = RunBrowserScriptCommandAsync(id)
            : null;

    /// <summary>表示中のページでスクリプトを走らせる。先にページへフォーカスを渡す——<c>navigator.clipboard</c> は
    /// フォーカスの無い文書からは書けないので、パレットから呼んだときにブックマークレットのコピーが落ちる。</summary>
    private async Task RunBrowserScriptCommandAsync(string id) {
        if (_settings.BrowserScripts.FirstOrDefault(c => c.Id == id) is not { } command)
            return;
        if (BrowserUserScript.Prepare(command.Script) is not { } script) {
            ToastService.Info($"「{CommandCatalog.BrowserScriptTitle(command)}」はスクリプトが空です。");
            return;
        }
        if (_activeBrowserTab?.View is not { } view || view.TryCore() is not { } core) {
            ToastService.Info("ブラウザでページを開いてから実行してください。");
            return;
        }
        try {
            view.Focus();
            await core.ExecuteScriptAsync(script);
        } catch (Exception ex) {
            ToastService.Error($"「{CommandCatalog.BrowserScriptTitle(command)}」を実行できませんでした：{ex.Message}");
        }
    }

    private void CloseActiveBrowserTab() {
        if (_activeBrowserTab is { } tab)
            _ = CloseBrowserTabAsync(tab.Id);
    }
}
