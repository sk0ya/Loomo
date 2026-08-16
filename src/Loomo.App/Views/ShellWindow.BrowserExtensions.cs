namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: ブラウザペインの<b>拡張機能</b>と<b>保存済みログイン情報</b>（設計書 §21.5.2）。
///
/// <para>どちらも WebView2 の<b>プロファイル</b>（＝アプリ単位で共有している
/// <c>%APPDATA%/Loomo/WebView2</c>）に属する話で、タブ1枚の都合ではない。プロファイルへ触るには
/// 実体化済みの <c>CoreWebView2</c> が要るので、ここはシェルの仕事になる——VM 側
/// （<see cref="BrowserViewModel"/>）は一覧と入力だけを持ち、実行はイベントでこちらへ委ねる
/// （ブックマークやダウンロードと同じ分担）。</para>
/// </summary>
public partial class ShellWindow {
    private readonly BrowserExtensionStore _extensionStore = new();
    private LoomoWebView2? _extensionPopupView;

    /// <summary>ポップアップの <c>CoreWebView2</c> に外へ出る道を結ぶ仕事（結ぶのは一度だけ）。
    /// <b>bool の旗ではなく仕事そのものを覚える</b>——旗だと、仕込みを待っている間に来た2回目が
    /// 「済んでいる」と読んで先に遷移してしまい、防ぎたかった「最初の1回だけ歯車が効かない」が起きる。</summary>
    private Task? _extensionPopupBridge;

    /// <summary>促しバーを × で閉じられた拡張機能。別のページへ移れば忘れる（その場限りの黙認）。</summary>
    private string? _dismissedStoreExtensionId;

    /// <summary>大きさを合わせるための開き直しの最中（<see cref="OnBrowserExtensionPopupClosed"/> を黙らせる）。</summary>
    private bool _resizingExtensionPopup;

    private void InitializeBrowserExtras() {
        var vm = _vm.Browser;
        vm.ExtensionsRefreshRequested += (_, _) => _ = RefreshBrowserExtensionsAsync();
        vm.ExtensionInstallRequested += (_, input) => _ = InstallBrowserExtensionFromStoreAsync(input);
        vm.ExtensionFolderInstallRequested += (_, _) => _ = InstallBrowserExtensionFromFolderAsync();
        vm.ExtensionEnableChanged += (_, item) => _ = SetBrowserExtensionEnabledAsync(item);
        vm.ExtensionRemoveRequested += (_, item) => _ = RemoveBrowserExtensionAsync(item);
        vm.ExtensionPopupRequested += (_, item) => OpenBrowserExtensionPopup(item);
        vm.ExtensionStoreInstallRequested += (_, _) => _ = InstallBrowserExtensionFromCurrentPageAsync();
        vm.ExtensionPromptDismissed += (_, _) => OnBrowserExtensionPromptDismissed();
        vm.PasswordsRefreshRequested += (_, _) => _ = LoadSavedPasswordsAsync();
        vm.PasswordsClearRequested += (_, _) => _ = ClearSavedPasswordsAsync();
    }

    /// <summary>プロファイルを触るための足場。タブが1枚も実体化していないと <c>CoreWebView2</c> が無いので、
    /// 必要なら<b>いま開いているタブを実体化してから</b>返す（拡張機能の一覧を見たいだけで
    /// ブラウザを一度表示させられるのは不親切）。</summary>
    private async Task<CoreWebView2Profile?> EnsureBrowserProfileAsync() {
        var tab = _activeBrowserTab ?? _browserTabs.FirstOrDefault();
        if (tab is null)
            return null;
        await EnsureBrowserRealizedAsync(tab);
        return tab.View.CoreWebView2?.Profile;
    }

    // ── 拡張機能 ───────────────────────────────────────────────────────
    private async Task RefreshBrowserExtensionsAsync() {
        var vm = _vm.Browser;
        if (await EnsureBrowserProfileAsync() is not { } profile) {
            vm.ExtensionStatus = "ブラウザタブを開くと一覧を取得します。";
            return;
        }
        try {
            var installed = await profile.GetBrowserExtensionsAsync();
            // 削除した直後は WebView2 がフォルダーを掴んでいて消せないことがある。一覧を開くたびに
            // 取り残しを拾い直す（起動し直したあとなら確実に消える）。
            // <b>導入の最中は掃除しない</b>——記録が書かれるのは登録が済んだ後なので、
            // 展開中のフォルダーが「記録に無い＝取り残し」に見えて、入れている最中のものを消してしまう。
            if (!vm.IsExtensionsBusy)
                _extensionStore.CleanOrphanFolders();
            var records = _extensionStore.LoadRecords();
            vm.SetExtensions(installed.Select(extension => BuildExtensionViewModel(extension, records)));
            vm.ExtensionStatus = installed.Count == 0
                ? "拡張機能はまだありません。ストアの URL か ID、または展開済みフォルダーから追加できます。"
                : "";
        } catch (Exception ex) {
            vm.ExtensionStatus = $"一覧を取得できませんでした: {ex.Message}";
        }
    }

    /// <summary>WebView2 が返す拡張機能に、こちらが覚えている出所（フォルダー）を突き合わせて行を作る。
    /// ボタン（ポップアップ）と設定画面の有無は manifest からしか分からない——WebView2 は拡張機能の UI を
    /// 描いてくれないので、<b>ホスト側で manifest を読んで自分で開く</b>ほかない。</summary>
    private static BrowserExtensionViewModel BuildExtensionViewModel(
        CoreWebView2BrowserExtension extension, List<BrowserExtensionRecord> records) {
        var record = records.FirstOrDefault(r => string.Equals(r.Id, extension.Id, StringComparison.OrdinalIgnoreCase));
        var manifest = record?.FolderPath is { Length: > 0 } folder
            ? BrowserExtensionStore.ReadManifest(folder)
            : null;
        return new BrowserExtensionViewModel(extension.IsEnabled) {
            Id = extension.Id,
            // 表示名は WebView2 が返すもの（manifest の __MSG_…__ を解いた地域化済みの名前）を使う。
            Name = string.IsNullOrWhiteSpace(extension.Name) ? extension.Id : extension.Name,
            Version = manifest?.Version,
            FolderPath = record?.FolderPath,
            IconPath = ResolveExtensionAsset(record?.FolderPath, manifest?.IconPath),
            PopupUrl = ExtensionPageUrl(extension.Id, manifest?.PopupPath),
            OptionsUrl = ExtensionPageUrl(extension.Id, manifest?.OptionsPath),
        };
    }

    /// <summary>manifest が指すページ（ポップアップ・設定画面）の <c>chrome-extension://</c> URL。</summary>
    private static string? ExtensionPageUrl(string id, string? path)
        => string.IsNullOrWhiteSpace(path) ? null : $"chrome-extension://{id}/{path.TrimStart('/')}";

    // ── ストアから追加する（本線） ─────────────────────────────────────
    /// <summary>ページの「Chrome に追加」を横取りしてこちらの導入へ回す注入スクリプト。
    /// <b>ストアの実物のボタンは WebView2 では何も起こせない</b>（Chromium の導入 API が無い）ので、
    /// 押しても黙って終わるより、こちらで受けたほうがよい。
    ///
    /// <para>掴み方は<b>ボタンの文言</b>。ストアのページは作りが頻繁に変わるうえ、実体は
    /// カスタム要素で安定した id も class も持たないため、構造で掴むと簡単に外れる。
    /// 文言で拾って、外れたら促しバーの「追加」が残る、という二段にしてある。</para></summary>
    private const string StoreInstallHookScript = """
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

    /// <summary>ストアの拡張機能ページを開いたら促しバーを出し、ページ側のボタンも横取りする。
    /// ストアは SPA なので、ページ内の遷移（<c>SourceChanged</c>）でも呼ばれる。</summary>
    private void EvaluateBrowserExtensionPrompt(BrowserTab tab) {
        if (!ReferenceEquals(_activeBrowserTab, tab))
            return;
        var vm = _vm.Browser;
        if (!BrowserExtensionStore.TryParseStoreDetail(BrowserUrlOf(tab), out var storeId, out _)) {
            vm.CloseExtensionPrompt();
            return;
        }
        // ページ側のボタンの横取りは、バーを閉じられていても続ける（閉じたのは促しであって、
        // ストアの「追加」を押す気が無くなったわけではない）。
        if (tab.View.CoreWebView2 is { } core)
            _ = core.ExecuteScriptAsync(StoreInstallHookScript);
        // 別の拡張機能のページへ移ったら、前のページで閉じたことは忘れる。
        if (string.Equals(_dismissedStoreExtensionId, storeId, StringComparison.OrdinalIgnoreCase))
            return;
        _dismissedStoreExtensionId = null;
        var installed = _extensionStore.LoadRecords()
            .Any(r => string.Equals(r.StoreId, storeId, StringComparison.OrdinalIgnoreCase));
        vm.ShowExtensionPrompt(StoreExtensionName(tab), installed);
    }

    /// <summary>× で閉じられたら、そのページにいる間は出し直さない。</summary>
    private void OnBrowserExtensionPromptDismissed() {
        _dismissedStoreExtensionId =
            BrowserExtensionStore.TryParseStoreDetail(BrowserUrlOf(_activeBrowserTab), out var id, out _)
                ? id
                : null;
    }

    /// <summary>促しバーに出す名前。ストアの題は「uBlock Origin - Chrome ウェブストア」のような形なので、
    /// 区切りの手前だけを採る（取れなければ何も足さない見出しにする）。</summary>
    private static string StoreExtensionName(BrowserTab tab) {
        var title = tab.View.CoreWebView2?.DocumentTitle ?? "";
        var cut = title.Split([" - ", " – ", " | "], StringSplitOptions.None)[0].Trim();
        return cut.Length > 0 ? cut : "この拡張機能";
    }

    /// <summary>ページ側のボタンからの合図を受ける。<b>ID は URL から取り直す</b>——
    /// ページから渡された値を信用して導入先を決めない。
    ///
    /// <para>合図の<b>出どころも見る</b>（<c>e.Source</c> がストアの拡張機能ページか）。
    /// この合図はタブの中のどのフレームからも送れるので、見ない場合はストアのページに載った
    /// 第三者の iframe が<b>クリック無しに導入を起こせる</b>——横取りしているのは
    /// 「使う側がストアの追加ボタンを押した」という出来事であって、ページからの依頼ではない。</para></summary>
    private void OnBrowserWebMessageReceived(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs e) {
        // タブで開いた設定画面も、その中から別のページ（説明ページ・別の設定タブ）を開こうとする。
        if (ExtensionPageBridge.TryReadOpenRequest(e.WebMessageAsJson, e.Source, out var target)) {
            OpenExtensionPageInBrowserTab(target);
            return;
        }
        try {
            var outer = JsonSerializer.Deserialize<JsonElement>(e.WebMessageAsJson);
            // postMessage(string) は JSON 文字列として届くので、二重に解く。
            var payload = outer.ValueKind == JsonValueKind.String
                ? JsonSerializer.Deserialize<JsonElement>(outer.GetString()!)
                : outer;
            // 型まで見てから読む。ページは何でも送れるので、文字列前提で GetString() を呼ぶと
            // `{"loomo":1}` ひとつでこのイベント（＝アプリ）が落ちる。
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("loomo", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != "installExtension")
                return;
        } catch (JsonException) {
            return;   // ページが送る他の web message は素通しする
        }
        if (!BrowserExtensionStore.TryParseStoreDetail(e.Source, out _, out _))
            return;
        if (ReferenceEquals(_activeBrowserTab, tab))
            _ = InstallBrowserExtensionFromCurrentPageAsync();
    }

    /// <summary>いま見ているストアページの拡張機能を入れる（促しバーの「追加」とページ側のボタンの共通の口）。</summary>
    private async Task InstallBrowserExtensionFromCurrentPageAsync() {
        var url = BrowserUrlOf(_activeBrowserTab);
        if (!BrowserExtensionStore.TryParseStoreDetail(url, out _, out _)) {
            ToastService.Info("拡張機能のページで押してください。");
            return;
        }
        _vm.Browser.CloseExtensionPrompt();
        ToastService.Info("拡張機能を取得しています…");
        await InstallBrowserExtensionFromStoreAsync(url!);
        // 一覧は閉じてあるので、失敗の理由は状態文に書かれても<b>誰も見ていない</b>。
        // 文言で選り分けず、残っている状態文はそのままトーストへ出す（成功なら空になっている）。
        if (_vm.Browser.ExtensionStatus is { Length: > 0 } status)
            ToastService.Error(status);
    }

    /// <summary>manifest が指す資産（アイコン）の実ファイルを引く。存在しないものは null にして、
    /// 一覧側で場所ごと詰める（欠けた画像の枠が並ぶより、無いなら無いで詰める方が読める）。</summary>
    private static string? ResolveExtensionAsset(string? folderPath, string? relativePath) {
        if (folderPath is not { Length: > 0 } || relativePath is not { Length: > 0 })
            return null;
        var full = Path.Combine(folderPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? full : null;
    }

    /// <summary>ストアの URL か ID から入れる。crx を取って展開し、そのフォルダーを登録する
    /// （<c>AddBrowserExtensionAsync</c> は展開済みフォルダーしか受け付けない）。</summary>
    private async Task InstallBrowserExtensionFromStoreAsync(string input) {
        var vm = _vm.Browser;
        if (!BrowserExtensionStore.TryParseStoreId(input, out var storeId, out var kind)) {
            vm.ExtensionStatus = "ストアの URL か、32 文字の拡張機能 ID を入れてください。";
            return;
        }
        if (await EnsureBrowserProfileAsync() is not { } profile) {
            vm.ExtensionStatus = "ブラウザタブを開いてから追加してください。";
            return;
        }
        vm.IsExtensionsBusy = true;
        vm.ExtensionStatus = "取得しています…";
        try {
            var version = BrowserVersionNumber();
            var extracted = await _extensionStore.DownloadAsync(storeId, kind, version);
            await AddExtensionFolderAsync(profile, extracted.Directory, storeId, kind);
        } catch (Exception ex) {
            vm.ExtensionStatus = $"追加できませんでした: {ex.Message}";
        } finally {
            vm.IsExtensionsBusy = false;
        }
    }

    /// <summary>展開済みフォルダーを選んで入れる（自分で作った拡張機能・手元で展開した crx）。
    /// <b>フォルダーは複製しない</b>——使う側の持ち物で、更新もその場で行われるべきものだから。</summary>
    private async Task InstallBrowserExtensionFromFolderAsync() {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "拡張機能のフォルダー（manifest.json のある場所）" };
        if (dialog.ShowDialog(this) != true)
            return;
        var vm = _vm.Browser;
        if (!File.Exists(Path.Combine(dialog.FolderName, "manifest.json"))) {
            vm.ExtensionStatus = "そのフォルダーに manifest.json がありません。";
            return;
        }
        if (await EnsureBrowserProfileAsync() is not { } profile) {
            vm.ExtensionStatus = "ブラウザタブを開いてから追加してください。";
            return;
        }
        vm.IsExtensionsBusy = true;
        try {
            await AddExtensionFolderAsync(profile, dialog.FolderName, storeId: null, kind: null);
        } catch (Exception ex) {
            vm.ExtensionStatus = $"追加できませんでした: {ex.Message}";
        } finally {
            vm.IsExtensionsBusy = false;
        }
    }

    private async Task AddExtensionFolderAsync(
        CoreWebView2Profile profile, string folderPath, string? storeId, BrowserExtensionStoreKind? kind) {
        var added = await profile.AddBrowserExtensionAsync(folderPath);
        var remembered = _extensionStore.Remember(new BrowserExtensionRecord {
            Id = added.Id,
            FolderPath = folderPath,
            StoreId = storeId,
            StoreKind = kind?.ToString(),
            Name = added.Name,
        });
        await RefreshBrowserExtensionsAsync();
        _vm.Browser.ExtensionInput = "";
        // 内容スクリプトは<b>次に読み込むページから</b>入る。開いたままのページで何も起きないのを
        // 「入っていない」と読まれないように、ここで言っておく。
        ToastService.Success($"「{added.Name}」を追加しました。開いているページは再読み込みしてください。");
        // 出所を書けなかったとき（記録ファイルが壊れている等）は黙らない——拡張機能は動くが、
        // ボタンも設定画面も出ず、掃除も止まったままになる。
        if (!remembered)
            _vm.Browser.ExtensionStatus =
                "出所を記録できませんでした（browser-extensions.json）。ボタンや設定画面は出ません。";
    }

    /// <summary>有効/無効を WebView2 へ流す。<b>通らなかったらチェックを戻す</b>——
    /// 表示だけ切り替わったままだと、無効にしたつもりの拡張機能が動き続ける
    /// （しかも次の一覧の取り直しで黙って元へ戻り、操作が無かったことになる）。</summary>
    private async Task SetBrowserExtensionEnabledAsync(BrowserExtensionViewModel item) {
        var requested = item.IsEnabled;
        if (await FindExtensionAsync(item.Id) is not { } extension) {
            item.RevertEnabled(!requested);
            _vm.Browser.ExtensionStatus = "切り替えられませんでした（拡張機能が見つかりません）。";
            return;
        }
        try {
            await extension.EnableAsync(requested);
        } catch (Exception ex) {
            item.RevertEnabled(!requested);
            _vm.Browser.ExtensionStatus = $"切り替えられませんでした: {ex.Message}";
        }
    }

    private async Task RemoveBrowserExtensionAsync(BrowserExtensionViewModel item) {
        if (await FindExtensionAsync(item.Id) is not { } extension)
            return;
        try {
            await extension.RemoveAsync();
            // 実体フォルダーの後始末は、こちらが展開したものだけ（フォルダー指定のものは残す）。
            // <b>導入の最中は掃除しない</b>——記録が書かれるのは登録が済んだ後なので、展開中の
            // フォルダーが「記録に無い＝取り残し」に見えて、入れている最中のものを消してしまう
            // （一覧の取り直しと同じ用心。掃除は次に一覧を開いたときに回ってくる）。
            _extensionStore.Forget(item.Id, cleanFolders: !_vm.Browser.IsExtensionsBusy);
            await RefreshBrowserExtensionsAsync();
            ToastService.Info($"「{item.Name}」を削除しました。");
        } catch (Exception ex) {
            _vm.Browser.ExtensionStatus = $"削除できませんでした: {ex.Message}";
        }
    }

    private async Task<CoreWebView2BrowserExtension?> FindExtensionAsync(string id) {
        if (await EnsureBrowserProfileAsync() is not { } profile)
            return null;
        try {
            var installed = await profile.GetBrowserExtensionsAsync();
            return installed.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        } catch {
            return null;
        }
    }

    /// <summary>crx の配布 URL に載せるブラウザの版。<c>BrowserVersionString</c> は
    /// 「120.0.0.0 dev」のようにチャンネル名が付くので数字だけ取り出す。</summary>
    private static string BrowserVersionNumber() {
        try {
            var number = CoreWebView2Environment.GetAvailableBrowserVersionString().Split(' ')[0];
            return number.Length > 0 ? number : "120.0.0.0";
        } catch {
            return "120.0.0.0";
        }
    }

    /// <summary>拡張機能のボタン（ポップアップ UI）を開く。WebView2 は拡張機能のツールバーを描かないので、
    /// <c>chrome-extension://&lt;ID&gt;/popup.html</c> を<b>こちらのポップアップに載せた WebView2 で開く</b>——
    /// これが無いと Bitwarden や 1Password は「入っているが触れない」ままになる。</summary>
    private void OpenBrowserExtensionPopup(BrowserExtensionViewModel item) {
        if (item.PopupUrl is { Length: > 0 } url)
            _ = OpenBrowserExtensionPopupAsync(url);
    }

    /// <summary>器を先に出してから中身を読み込む。<b>実体化と仕込みが済むまで遷移させない</b>——
    /// ポップアップの中の歯車（<see cref="ExtensionPageBridge"/>）はドキュメント生成時の仕込みなので、
    /// 先に <c>Source</c> を立てて読み込みを始めてしまうと、最初の1回だけ効かない拡張機能ができる。</summary>
    private async Task OpenBrowserExtensionPopupAsync(string url) {
        var view = _extensionPopupView ??= CreateExtensionPopupView();
        BrowserExtensionPopupHost.Child = view;
        BrowserExtensionPopup.IsOpen = true;
        if (view.CoreWebView2 is null) {
            try {
                await view.EnsureCoreWebView2Async();
            } catch {
                _vm.Browser.ExtensionStatus = "この拡張機能の画面を開けませんでした。";
                return;
            }
        }
        if (view.CoreWebView2 is not { } core)
            return;
        await (_extensionPopupBridge ??= ConfigureExtensionPopupCoreAsync(core));
        TryNavigateBrowserCore(core, url);
    }

    private LoomoWebView2 CreateExtensionPopupView() {
        var view = new LoomoWebView2 {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
            CreationProperties = CreateWebViewCreationProperties(),
        };
        // ポップアップの中身は拡張機能が決める（大きさも中身次第）ので、読み込み後に測って器を合わせる。
        view.NavigationCompleted += async (_, _) => await FitExtensionPopupAsync(view);
        return view;
    }

    /// <summary>ポップアップの中身から<b>外へ出る道</b>を結ぶ（一度だけ）。ポップアップの歯車やリンクは
    /// タブを開こうとするので、受け止めないと押しても何も起きない——それが「入っているが設定できない」の
    /// 正体だった（§21.5.2）。</summary>
    private async Task ConfigureExtensionPopupCoreAsync(CoreWebView2 core) {
        core.NewWindowRequested += (_, e) => {
            e.Handled = true;
            OpenExtensionPageInBrowserTab(e.Uri);
        };
        core.WebMessageReceived += (_, e) => {
            if (ExtensionPageBridge.TryReadOpenRequest(e.WebMessageAsJson, e.Source, out var target))
                OpenExtensionPageInBrowserTab(target);
        };
        try {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ExtensionPageBridge.Script);
        } catch {
            // 仕込めなくても、ポップアップ自体は開く（歯車が無反応なままになるだけ）。
        }
    }

    /// <summary>拡張機能のページから頼まれた行き先を、部屋のブラウザタブで開く。
    /// ポップアップは畳む——開いたページの上に小窓が残ると、どちらを見ているのか分からなくなる。</summary>
    private void OpenExtensionPageInBrowserTab(string url) {
        BrowserExtensionPopup.IsOpen = false;
        _vm.Browser.IsExtensionsOpen = false;
        _ = OpenBrowserLibraryUrlAsync(url, newTab: true);
    }

    /// <summary>拡張機能のポップアップの<b>中身が欲しがっている大きさ</b>を測る。
    ///
    /// <para><c>documentElement.scrollWidth</c> だけでは測れない——<b>ビューポートより小さくならない</b>ので、
    /// 器の大きさ（既定の 400×560）がそのまま返り、いつまでも縮まない。実際に見えているのは
    /// 本文だけで、残りは WebView2 の地の色（暗い面）が広がっているだけ、という見え方になる。
    /// なので<b>本文の矩形</b>を基準にし、はみ出しているとき（＝ビューポートを超える <c>scroll*</c>）だけ
    /// そちらを採る。</para>
    ///
    /// <para><c>JSON.stringify</c> は挟まない。ExecuteScriptAsync は結果を JSON にして返すので、
    /// 文字列を返すと二重エンコード（`"\"[400,600]\""`）になり配列として解けない
    /// （`BrowserService.DecodeScriptString` に同じ罠の記録がある）。</para></summary>
    private const string PopupMeasureScript = """
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

    private async Task FitExtensionPopupAsync(LoomoWebView2 view) {
        if (view.CoreWebView2 is not { } core)
            return;
        // 閉じたときの <c>about:blank</c> でも遷移完了は来る。白紙を測ると器が下限（240×120）まで
        // 縮み、<b>次に開いたポップアップがその小ささで出る</b>（測り直しの開き直しでやっと戻る）。
        if (!BrowserExtensionPopup.IsOpen || core.Source is null or "" or "about:blank")
            return;
        try {
            var json = await core.ExecuteScriptAsync(PopupMeasureScript);
            if (JsonSerializer.Deserialize<double[]>(json) is not { Length: 2 } size)
                return;
            BrowserExtensionPopupHost.Width = Math.Clamp(size[0], 240, 800);
            BrowserExtensionPopupHost.Height = Math.Clamp(size[1], 120, 640);
            ResizeBrowserExtensionPopupWindow();
        } catch {
            // 測れなくても既定の大きさで出す（何も出ないよりまし）。
        }
    }

    /// <summary><see cref="Popup"/> の窓は<b>開いた時点の大きさのまま</b>で、中身を縮めても付いてこない
    /// （<c>AllowsTransparency=False</c> なので、余った面が黒い帯として残る。位置を動かして
    /// 作り直させる手も効かなかった）。開き直すのが確実——中身の WebView2 は使い回すので、
    /// 読み込み直しは起きない。</summary>
    private void ResizeBrowserExtensionPopupWindow() {
        if (!BrowserExtensionPopup.IsOpen)
            return;
        _resizingExtensionPopup = true;
        BrowserExtensionPopup.IsOpen = false;
        // <b>同じ呼び出しの中で開き直しても窓は作り直されない</b>（閉じる処理が走り切る前に
        // 開いた状態に戻るだけ）。次のディスパッチまで待ってから開く。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            BrowserExtensionPopup.IsOpen = true;
            _resizingExtensionPopup = false;
        }));
    }

    /// <summary>ポップアップを閉じたら中身を止める（裏で動き続けないように about:blank へ戻す）。
    /// <b>大きさを合わせるための開き直しでは止めない</b>——止めると測った直後に白紙になる。</summary>
    private void OnBrowserExtensionPopupClosed(object? sender, EventArgs e) {
        if (_resizingExtensionPopup)
            return;
        if (_extensionPopupView?.CoreWebView2 is { } core)
            TryNavigateBrowserCore(core, "about:blank");
    }

    // ── 保存済みログイン情報 ───────────────────────────────────────────
    /// <summary>一覧を読む。<b>UI スレッドから外す</b>——DB のコピー・SQLite・復号が入るので、
    /// 一覧を開いた瞬間に部屋ごと固まって見える。</summary>
    private async Task LoadSavedPasswordsAsync() {
        var vm = _vm.Browser;
        vm.PasswordStatus = "読み込んでいます…";
        var result = await Task.Run(() => SavedPasswordStore.ForUserDataFolder(WebViewUserDataFolder).Load());
        // 読んでいる間に閉じられていたら、平文を VM へ載せない。
        if (!vm.IsPasswordsOpen)
            return;
        vm.SetPasswords(
            result.Items
                .Select(p => new SavedPasswordViewModel {
                    Origin = p.Origin, Host = p.Host, Username = p.Username, Password = p.Password,
                })
                .ToList(),
            result.Error);
    }

    /// <summary>保存済みのログイン情報を全部消す。<b>消すのはブラウザ自身にやらせる</b>——
    /// Login Data は稼働中の WebView2 が掴んでいて、こちらから書き換えるとプロファイルを壊す。</summary>
    private async Task ClearSavedPasswordsAsync() {
        if (MessageBox.Show(this,
                "このブラウザに保存されたログイン情報をすべて削除します。元に戻せません。",
                "保存済みのログイン情報を削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
            return;
        if (await EnsureBrowserProfileAsync() is not { } profile) {
            _vm.Browser.PasswordStatus = "ブラウザタブを開いてから実行してください。";
            return;
        }
        try {
            await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.PasswordAutosave);
            await LoadSavedPasswordsAsync();
            ToastService.Info("保存済みのログイン情報を削除しました。");
        } catch (Exception ex) {
            _vm.Browser.PasswordStatus = $"削除できませんでした: {ex.Message}";
        }
    }
}
