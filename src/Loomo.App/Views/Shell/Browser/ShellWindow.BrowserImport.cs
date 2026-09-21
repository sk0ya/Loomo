using System.Runtime.Versioning;

namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 他のブラウザからの取り込み（設計書 §21.5.4）。
///
/// <para>取り込みは<b>行き先が3つに割れる</b>のがそのまま実装の形になっている：
/// ブックマークと履歴は VM が持つ <c>browser.json</c> へ、Cookie は WebView2 の
/// <c>CookieManager</c> へ、パスワードは<b>次の起動</b>での <c>Login Data</c> 書き込みへ。
/// 三者は書ける相手も書けるタイミングも違うので、1本の「適用」にまとめず、
/// 結果だけを1つの文にまとめて伝える。</para>
///
/// <para><b>キャッシュは扱わない</b>。持ち込んでも当たらず、プロファイルを壊す危険だけが残る——
/// 「ログインしたままにしたい」は Cookie が満たす（<see cref="ApplyImportedCookiesAsync"/>）。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ShellWindow {
    private void InitializeBrowserImport() {
        var import = _vm.Browser.Import;
        var workflow = new BrowserImportWorkflowController(
            _vm.Browser, ApplyImportedCookiesAsync, ToastService.Success);
        import.SourcesRefreshRequested += (_, _) => _ = workflow.RefreshSourcesAsync();
        import.ImportRequested += (_, e) => _ = workflow.RunAsync(e.Profile, e.Selection);
        import.CsvImportRequested += (_, _) => _ = ImportPasswordsFromCsvAsync(workflow);
    }

    /// <summary>Cookie を<b>ブラウザ自身に</b>入れさせる（DB へ直接書かない）。
    /// <c>Login Data</c> と違って CookieManager という正規の入口があるので、稼働中でも入れられる。
    /// 1件ごとに例外を握るのは、期限切れや壊れたドメインの1件で残り全部を落とさないため。</summary>
    private async Task<int> ApplyImportedCookiesAsync(IReadOnlyList<ImportedCookie> cookies) {
        if (await EnsureBrowserProfileAsync() is not { } profile)
            return 0;
        return BrowserCookieImporter.Apply(profile.CookieManager, cookies);
    }

    /// <summary>ブラウザが書き出した CSV からパスワードを取り込む。
    /// <b>Chrome から移す唯一の道</b>——アプリ束縛暗号の項目は外から解けないので、
    /// ブラウザ自身に正規の手続き（Windows のログイン認証つき）で書き出させたものを受ける。</summary>
    private async Task ImportPasswordsFromCsvAsync(BrowserImportWorkflowController workflow) {
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Title = "ブラウザが書き出したパスワード CSV",
            Filter = "CSV ファイル|*.csv|すべてのファイル|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        await workflow.ImportPasswordsFromCsvAsync(dialog.FileName);
    }
}
