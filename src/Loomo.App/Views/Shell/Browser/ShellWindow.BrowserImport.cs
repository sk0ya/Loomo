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
        import.SourcesRefreshRequested += (_, _) => _ = RefreshBrowserImportSourcesAsync();
        import.ImportRequested += (_, e) => _ = RunBrowserImportAsync(e.Profile, e.Selection);
        import.CsvImportRequested += (_, _) => _ = ImportPasswordsFromCsvAsync();
    }

    /// <summary>取り込み元の一覧を作る。<b>そのブラウザ固有の但し書きも一緒に作る</b>——
    /// 「起動中だと Cookie は読めない」「Chrome のパスワードはアプリ束縛暗号で読めない」は
    /// 押した後ではなく選ぶ前に見えている必要がある。</summary>
    private async Task RefreshBrowserImportSourcesAsync() {
        var import = _vm.Browser.Import;
        import.IsBusy = true;
        try {
            var sources = await Task.Run(() => ChromiumBrowsers.Detect()
                .SelectMany(ChromiumBrowsers.ProfilesOf)
                .Select(DescribeImportSource)
                .ToList());
            import.SetSources(sources, sources.Count == 0
                ? "取り込めるブラウザが見つかりませんでした。"
                : "");
        } finally {
            import.IsBusy = false;
        }
    }

    /// <summary>その相手で起きることを1行で。<b>数えられることだけを言う</b>——
    /// 「たぶん移せます」ではなく、鍵の形とファイルの状態から分かることだけを書く。</summary>
    private static BrowserImportSourceViewModel DescribeImportSource(ChromiumProfileRef profile) {
        var notes = new List<string>();
        var cookiesLocked = ChromiumImportReader.IsCookieDatabaseLocked(profile.Path);
        var appBound = ChromiumCrypto.TryOpen(profile.Browser.UserDataFolder, out var crypto, out _) && crypto!.IsAppBound;
        var bookmarks = ChromiumImportReader.ReadBookmarks(profile.Path);
        if (cookiesLocked)
            notes.Add($"Cookie を取り込むには {profile.Browser.DisplayName} を終了してください");
        if (appBound)
            notes.Add("このブラウザは保存内容をアプリ束縛暗号で保護しているため、"
                + "パスワードと Cookie は取り込めません（パスワードは CSV 書き出しから）");
        if (!string.IsNullOrEmpty(bookmarks.Error))
            notes.Add($"ブックマークを読めません: {bookmarks.Error}");
        return new BrowserImportSourceViewModel {
            Profile = profile,
            BookmarkCount = bookmarks.Count,
            CanImportPasswords = !appBound,
            CanImportCookies = !appBound && !cookiesLocked,
            Note = string.Join(" / ", notes),
        };
    }

    /// <summary>取り込みを実行して、結果を1つの文にまとめる。読み取りは重い（SQLite のコピー・
    /// 復号が数千件ぶん）ので <b>UI スレッドから外す</b>。</summary>
    private async Task RunBrowserImportAsync(ChromiumProfileRef profile, BrowserImportSelection selection) {
        var import = _vm.Browser.Import;
        import.IsBusy = true;
        import.Status = "取り込んでいます…";
        try {
            var harvest = await Task.Run(() => BrowserImportService.Harvest(profile, selection));

            var summary = new List<string>();
            var (bookmarks, history) = _vm.Browser.MergeImported(harvest.Bookmarks, harvest.History);
            var importedSomething = bookmarks > 0 || history > 0;
            if (selection.Bookmarks)
                summary.Add($"ブックマーク: {bookmarks} 件追加（{harvest.Bookmarks.Count} 件を読込）");
            if (selection.History)
                summary.Add($"履歴: {history} 件追加（{harvest.History.Count} 件を読込）");
            if (harvest.Cookies.Count > 0) {
                var applied = await ApplyImportedCookiesAsync(harvest.Cookies);
                summary.Add($"Cookie: {applied} 件反映");
                importedSomething |= applied > 0;
            }
            if (selection.Passwords && harvest.Passwords.Count > 0) {
                var queued = BrowserImportService.QueuePasswords(harvest.Passwords);
                // ここでは書けない（稼働中の WebView2 が Login Data を掴んでいる）ので、
                // 「入った」ではなく「次の起動で入る」と言う。嘘をつかないのがこの一行の役目。
                summary.Add($"パスワード: {queued} 件（次回起動時に取り込みます）");
                importedSomething |= queued > 0;
            }
            else if (selection.Passwords)
                summary.Add("パスワード: 0 件");
            if (selection.Cookies && harvest.Cookies.Count == 0)
                summary.Add("Cookie: 0 件");

            var notes = new List<string>(harvest.Errors);
            if (harvest.Blocked > 0)
                notes.Add($"{harvest.Blocked} 件はアプリ束縛暗号のため取り込めませんでした。");
            if (harvest.SkippedCookies > 0)
                notes.Add($"Cookie {harvest.SkippedCookies} 件は期限切れ・区画付き（埋め込み先ごとの Cookie）のため持ち込みませんでした。");

            import.Status = summary.Count == 0
                ? notes.Count == 0 ? "取り込むものがありませんでした。" : string.Join(" ", notes)
                : string.Join("・", summary) + "。"
                    + (notes.Count == 0 ? "" : " " + string.Join(" ", notes));
            if (importedSomething)
                ToastService.Success($"{profile.Label} から取り込みました。");
        } catch (Exception ex) {
            import.Status = $"取り込めませんでした: {ex.Message}";
        } finally {
            import.IsBusy = false;
        }
    }

    /// <summary>Cookie を<b>ブラウザ自身に</b>入れさせる（DB へ直接書かない）。
    /// <c>Login Data</c> と違って CookieManager という正規の入口があるので、稼働中でも入れられる。
    /// 1件ごとに例外を握るのは、期限切れや壊れたドメインの1件で残り全部を落とさないため。</summary>
    private async Task<int> ApplyImportedCookiesAsync(IReadOnlyList<ImportedCookie> cookies) {
        if (await EnsureBrowserProfileAsync() is not { } profile)
            return 0;
        var manager = profile.CookieManager;
        var applied = 0;
        foreach (var item in cookies) {
            try {
                var cookie = manager.CreateCookie(item.Name, item.Value, item.Domain, item.Path);
                cookie.IsSecure = item.IsSecure;
                cookie.IsHttpOnly = item.IsHttpOnly;
                cookie.SameSite = item.SameSite switch {
                    0 => CoreWebView2CookieSameSiteKind.None,
                    2 => CoreWebView2CookieSameSiteKind.Strict,
                    // 相手が持っていない（-1）ときは Chromium の既定と同じ Lax にする。
                    _ => CoreWebView2CookieSameSiteKind.Lax,
                };
                // 期限なし＝セッション Cookie。DateTime.MinValue がその印（Expires を触らない）。
                if (item.ExpiresUtc is { } expires)
                    cookie.Expires = expires;
                manager.AddOrUpdateCookie(cookie);
                applied++;
            } catch (Exception ex) when (ex is ArgumentException or COMException) {
            }
        }
        return applied;
    }

    /// <summary>ブラウザが書き出した CSV からパスワードを取り込む。
    /// <b>Chrome から移す唯一の道</b>——アプリ束縛暗号の項目は外から解けないので、
    /// ブラウザ自身に正規の手続き（Windows のログイン認証つき）で書き出させたものを受ける。</summary>
    private async Task ImportPasswordsFromCsvAsync() {
        var import = _vm.Browser.Import;
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Title = "ブラウザが書き出したパスワード CSV",
            Filter = "CSV ファイル|*.csv|すべてのファイル|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        import.IsBusy = true;
        try {
            var read = await Task.Run(() => ChromePasswordCsv.Read(dialog.FileName));
            if (read.Error is { } error) {
                import.Status = error;
                return;
            }
            var queued = BrowserImportService.QueuePasswords(read.Items);
            import.Status = queued == 0
                ? "CSV に取り込める行がありませんでした。"
                : $"パスワード {queued} 件を読み込みました（次回起動時に取り込みます）。"
                    + (read.Blocked == 0 ? "" : $" {read.Blocked} 行は形が合わず飛ばしました。");
            if (queued > 0)
                ToastService.Success($"パスワード {queued} 件を読み込みました。");
        } finally {
            import.IsBusy = false;
        }
    }
}
