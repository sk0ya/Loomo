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
    /// <summary>促しバーを × で閉じられた拡張機能。別のページへ移れば忘れる（その場限りの黙認）。</summary>
    private readonly BrowserExtensionPromptState _extensionPromptState = new();

    private BrowserExtensionPopupController? _extensionPopupController;

    private void InitializeBrowserExtras() {
        var vm = _vm.Browser;
        vm.ExtensionsRefreshRequested += (_, _) => _ = RefreshBrowserExtensionsAsync();
        vm.ExtensionInstallRequested += (_, input) => _ = InstallBrowserExtensionFromStoreAsync(input);
        vm.ExtensionFolderInstallRequested += (_, _) => _ = InstallBrowserExtensionFromFolderAsync();
        vm.ExtensionEnableChanged += (_, item) => _ = SetBrowserExtensionEnabledAsync(item);
        vm.ExtensionRemoveRequested += (_, item) => _ = RemoveBrowserExtensionAsync(item);
        vm.ExtensionPopupRequested += (_, item) =>
        {
            if (item.PopupUrl is { Length: > 0 } url)
                _ = ExtensionPopupController.OpenAsync(url);
        };
        vm.ExtensionStoreInstallRequested += (_, _) => _ = InstallBrowserExtensionFromCurrentPageAsync();
        vm.ExtensionPromptDismissed += (_, _) => OnBrowserExtensionPromptDismissed();
        vm.PasswordsRefreshRequested += (_, _) => _ = LoadSavedPasswordsAsync();
        vm.PasswordsClearRequested += (_, _) => _ = ClearSavedPasswordsAsync();
        InitializeBrowserImport();
    }

    /// <summary>プロファイルを触るための足場。タブが1枚も実体化していないと <c>CoreWebView2</c> が無いので、
    /// 必要なら<b>いま開いているタブを実体化してから</b>返す（拡張機能の一覧を見たいだけで
    /// ブラウザを一度表示させられるのは不親切）。</summary>
    private async Task<CoreWebView2Profile?> EnsureBrowserProfileAsync() {
        var tab = _activeBrowserTab ?? _browserTabs.FirstOrDefault();
        if (tab is null)
            return null;
        await EnsureBrowserRealizedAsync(tab);
        return tab.View.TryCore()?.Profile;
    }

    // ── 拡張機能 ───────────────────────────────────────────────────────
    private async Task RefreshBrowserExtensionsAsync() {
        var vm = _vm.Browser;
        if (await EnsureBrowserProfileAsync() is not { } profile) {
            vm.ExtensionStatus = "ブラウザタブを開くと一覧を取得します。";
            return;
        }
        var result = await BrowserExtensionListService.LoadAsync(profile, _extensionStore, vm.IsExtensionsBusy);
        if (result.Items is { } items)
            vm.SetExtensions(items);
        vm.ExtensionStatus = result.StatusText;
    }

    // ── ストアから追加する（本線） ─────────────────────────────────────
    /// <summary>ストアの拡張機能ページを開いたら促しバーを出し、ページ側のボタンも横取りする。
    /// ストアは SPA なので、ページ内の遷移（<c>SourceChanged</c>）でも呼ばれる。</summary>
    private void EvaluateBrowserExtensionPrompt(BrowserTab tab) {
        if (!ReferenceEquals(_activeBrowserTab, tab))
            return;
        var vm = _vm.Browser;
        var decision = BrowserExtensionPromptPolicy.Evaluate(
            _extensionPromptState, BrowserUrlOf(tab));
        if (!decision.IsStorePage) {
            vm.CloseExtensionPrompt();
            return;
        }
        // ページ側のボタンの横取りは、バーを閉じられていても続ける（閉じたのは促しであって、
        // ストアの「追加」を押す気が無くなったわけではない）。
        if (tab.View.TryCore() is { } core)
            _ = core.ExecuteScriptAsync(BrowserExtensionScripts.StoreInstallHook);
        // 別の拡張機能のページへ移ったら、前のページで閉じたことは忘れる。
        if (!decision.ShouldPrompt)
            return;
        var installed = BrowserExtensionPromptPolicy.IsInstalled(
            decision.StoreId!, _extensionStore.LoadRecords());
        vm.ShowExtensionPrompt(
            BrowserExtensionDisplayMapper.StoreName(tab.View.TryCore()?.DocumentTitle), installed);
    }

    /// <summary>× で閉じられたら、そのページにいる間は出し直さない。</summary>
    private void OnBrowserExtensionPromptDismissed() {
        BrowserExtensionPromptPolicy.Dismiss(_extensionPromptState, BrowserUrlOf(_activeBrowserTab));
    }

    private BrowserExtensionPopupController ExtensionPopupController => _extensionPopupController ??=
        new BrowserExtensionPopupController(
            BrowserExtensionPopup,
            BrowserExtensionPopupHost,
            CreateWebViewCreationProperties,
            OpenExtensionPageInBrowserTab,
            message => _vm.Browser.ExtensionStatus = message);

    /// <summary>ページ側のボタンからの合図を受ける。<b>ID は URL から取り直す</b>——
    /// ページから渡された値を信用して導入先を決めない。
    ///
    /// <para>合図の<b>出どころも見る</b>（<c>e.Source</c> がストアの拡張機能ページか）。
    /// この合図はタブの中のどのフレームからも送れるので、見ない場合はストアのページに載った
    /// 第三者の iframe が<b>クリック無しに導入を起こせる</b>——横取りしているのは
    /// 「使う側がストアの追加ボタンを押した」という出来事であって、ページからの依頼ではない。</para></summary>
    private void OnBrowserWebMessageReceived(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs e) {
        // タブで開いた設定画面も、その中から別のページ（説明ページ・別の設定タブ）を開こうとする。
        if (ExtensionPageBridgeService.TryReadOpenRequest(e.WebMessageAsJson, e.Source, out var target)) {
            OpenExtensionPageInBrowserTab(target);
            return;
        }
        if (!BrowserExtensionMessagePolicy.IsStoreInstallRequest(e.WebMessageAsJson, e.Source))
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

    /// <summary>ストアの URL か ID から入れる。crx を取って展開し、そのフォルダーを登録する
    /// （<c>AddBrowserExtensionAsync</c> は展開済みフォルダーしか受け付けない）。</summary>
    private async Task InstallBrowserExtensionFromStoreAsync(string input) {
        await BrowserExtensionInstallController.InstallFromStoreAsync(
            _vm.Browser, _extensionStore, input, EnsureBrowserProfileAsync,
            CompleteBrowserExtensionInstallAsync);
    }

    /// <summary>展開済みフォルダーを選んで入れる（自分で作った拡張機能・手元で展開した crx）。
    /// <b>フォルダーは複製しない</b>——使う側の持ち物で、更新もその場で行われるべきものだから。</summary>
    private async Task InstallBrowserExtensionFromFolderAsync() {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "拡張機能のフォルダー（manifest.json のある場所）" };
        if (dialog.ShowDialog(this) != true)
            return;
        await BrowserExtensionInstallController.InstallFromFolderAsync(
            _vm.Browser, _extensionStore, dialog.FolderName, EnsureBrowserProfileAsync,
            CompleteBrowserExtensionInstallAsync);
    }

    private async Task CompleteBrowserExtensionInstallAsync(BrowserExtensionRegistration registration) {
        await RefreshBrowserExtensionsAsync();
        _vm.Browser.ExtensionInput = "";
        // 内容スクリプトは<b>次に読み込むページから</b>入る。開いたままのページで何も起きないのを
        // 「入っていない」と読まれないように、ここで言っておく。
        var presentation = BrowserExtensionDisplayMapper.Install(registration);
        ToastService.Success(presentation.SuccessMessage);
        // 出所を書けなかったとき（記録ファイルが壊れている等）は黙らない——拡張機能は動くが、
        // ボタンも設定画面も出ず、掃除も止まったままになる。
        if (presentation.StatusWarning is { } warning)
            _vm.Browser.ExtensionStatus = warning;
    }

    /// <summary>有効/無効を WebView2 へ流す。<b>通らなかったらチェックを戻す</b>——
    /// 表示だけ切り替わったままだと、無効にしたつもりの拡張機能が動き続ける
    /// （しかも次の一覧の取り直しで黙って元へ戻り、操作が無かったことになる）。</summary>
    private async Task SetBrowserExtensionEnabledAsync(BrowserExtensionViewModel item) {
        var requested = item.IsEnabled;
        var error = await EnsureBrowserProfileAsync() is { } profile
            ? await BrowserExtensionManager.SetEnabledAsync(profile, item.Id, requested)
            : "切り替えられませんでした（拡張機能が見つかりません）。";
        if (error is not null) {
            item.RevertEnabled(!requested);
            _vm.Browser.ExtensionStatus = error;
        }
    }

    private async Task RemoveBrowserExtensionAsync(BrowserExtensionViewModel item) {
        if (await EnsureBrowserProfileAsync() is not { } profile)
            return;
        try {
            // 実体フォルダーの後始末は、こちらが展開したものだけ（フォルダー指定のものは残す）。
            // <b>導入の最中は掃除しない</b>——記録が書かれるのは登録が済んだ後なので、展開中の
            // フォルダーが「記録に無い＝取り残し」に見えて、入れている最中のものを消してしまう
            // （一覧の取り直しと同じ用心。掃除は次に一覧を開いたときに回ってくる）。
            var removed = await BrowserExtensionManager.RemoveAsync(
                _extensionStore, profile, item.Id, cleanFolders: !_vm.Browser.IsExtensionsBusy);
            if (!removed)
                return;
            await RefreshBrowserExtensionsAsync();
            ToastService.Info($"「{item.Name}」を削除しました。");
        } catch (Exception ex) {
            _vm.Browser.ExtensionStatus = $"削除できませんでした: {ex.Message}";
        }
    }

    /// <summary>拡張機能のページから頼まれた行き先を、部屋のブラウザタブで開く。
    /// ポップアップは畳む——開いたページの上に小窓が残ると、どちらを見ているのか分からなくなる。</summary>
    private void OpenExtensionPageInBrowserTab(string url) {
        BrowserExtensionPopup.IsOpen = false;
        _vm.Browser.IsExtensionsOpen = false;
        _ = OpenBrowserLibraryUrlAsync(url, newTab: true);
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
        vm.SetPasswords(SavedPasswordDisplayMapper.Map(result.Items), result.Error);
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
