
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: EditorSupport ペイン（Markdown プレビュー等の表示・スクロール同期）。
/// 自動表示はしない（明示操作で開いたときだけアクティブエディタに追従して描く）。</summary>
public partial class ShellWindow
{
    // エディタからの明示プレビュー要求：EditorSupport ペインを開いて内容を流し込む。 タイル表示なら Editor の右隣へ開き、ソロモードなら舞台へ立てる。
    private async Task OpenEditorSupportAsync(EditorTab sourceTab)
    {
        await SwitchEditorSupportSourceAsync(sourceTab, force: true);
        if (_stageActive)
            SetStagePane(PaneKind.EditorSupport);   // ソロは舞台へ立てる
        else
            ShowEditorSupportPane();                 // タイルは Editor の右隣へ開く
        await UpdateEditorSupportAsync();
        // 明示的なプレビュー要求＝「このファイルのプレビューを見に来た」地点。ペインフォーカスの デバウンス経路とは同一ファイルでデデュープされるので二重ドットにはならない。
        RecordTrailPreview(sourceTab);
    }

    // ヘッダー／コンテキストメニューの「戻る」。エディタのファイル履歴を 1 つ前へ戻す。
    private async void OnEditorSupportBack(object sender, RoutedEventArgs e) => await EditorSupportGoBackAsync();

    // マウスのサイドボタン（戻る=XButton1／進む=XButton2）でエディタのファイル履歴を行き来する。 Window レベルの PreviewMouseDown（トンネル）で各 WPF ペインより先に受ける。ブラウザ／プレビューの WebView2 エアスペース上ではマウスイベントが WPF へ来ないため効かない（＝WebView 自身の履歴が優先）。
    private void OnShellPreviewMouseNavigate(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            e.Handled = true;
            _ = EditorSupportGoBackAsync();
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            e.Handled = true;
            _ = EditorSupportGoForwardAsync();
        }
    }

    private Task EditorSupportGoBackAsync() => EditorSupportNavigateHistoryAsync(back: true);
    private Task EditorSupportGoForwardAsync() => EditorSupportNavigateHistoryAsync(back: false);

    // ファイル履歴を back の向きへ 1 つ移動する。移動先タブが開いていれば前面化し、 閉じていて実在すれば開き直す。消えたファイルは飛ばして次の履歴へ進む。移動中は _editorSupport.IsNavigating を立て、内部で走る SwitchEditorSupportSourceAsync の 履歴記録を抑止する（二重記録・forward 破棄の防止）。
    private async Task EditorSupportNavigateHistoryAsync(bool back)
    {
        _editorSupport.IsNavigating = true;
        try
        {
            while ((back ? _editorSupport.History.GoBack() : _editorSupport.History.GoForward()) is { } path)
            {
                var open = _editorTabs.FirstOrDefault(t =>
                    string.Equals(t.PeekFilePath, path, StringComparison.OrdinalIgnoreCase));
                if (open is not null)
                {
                    ActivateEditorTab(open.Id);
                    break;
                }

                if (File.Exists(path))
                {
                    await OpenFileInNewEditorTabAsync(path);
                    break;
                }
                // 消えたファイルは飛ばして次の履歴へ。
            }
        }
        finally
        {
            _editorSupport.IsNavigating = false;
        }

        UpdateEditorSupportNavAffordances();
    }

    // 戻る操作の可否を UI へ反映する（進む UI は無いので back ボタンの活性のみ）。
    private void UpdateEditorSupportNavAffordances()
    {
        if (EditorSupportBackButton is not null)
            EditorSupportBackButton.IsEnabled = _editorSupport.History.CanGoBack;
    }

    // EditorSupport の追従先エディタタブを切り替えて内容を更新する（同一タブなら何もしない）。
    private async Task SwitchEditorSupportSourceAsync(EditorTab sourceTab, bool force = false)
    {
        if (!_editorSupport.TryChangeSource(sourceTab, force, out var previous))
            return;

        if (previous is not null)
        {
            previous.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
            previous.Control.CaretMoved -= EditorSupportSource_CaretMoved;
        }
        // 追従先が変わる＝前ファイルの接続待ちポーリングは無効（新ファイルで作り直す）。
        StopCodeReadyRetry();

        UpdateEditorSupportNavAffordances();
        sourceTab.Control.ViewportScrolled += EditorSupportSource_ViewportScrolled;
        // コード解析（②）のキャレット追従。非コードや案内表示中は Schedule 側で無視される。
        sourceTab.Control.CaretMoved += EditorSupportSource_CaretMoved;
        UpdateEditorSupportPinToggle();

        await UpdateEditorSupportAsync();
    }

    // EditorSupport ヘッダーのピン：追従先タブを現在の対象へ固定／固定解除する。
    private async void OnToggleEditorSupportPin(object sender, RoutedEventArgs e)
    {
        _editorSupport.IsPinned = EditorSupportPinToggle.IsChecked == true;
        UpdateEditorSupportPinToggle();

        if (_editorSupport.IsPinned)
        {
            if (_editorSupport.Source is null && _activeEditorTab is not null)
                await SwitchEditorSupportSourceAsync(_activeEditorTab, force: true);
            return;
        }

        if (_activeEditorTab is not null)
            await SwitchEditorSupportSourceAsync(_activeEditorTab, force: true);
    }

    // EditorSupport ヘッダーの発表トグル：プレビューを発表モード（1枚ずつ）／縦並び全表示で切り替える。 共有 AiSettings の値を更新すると provider が次の描画から反映する。既定（OFF）は全スライド の縦並び表示。marp:true 文書は常に marp で描かれ、このトグルで発表⇔縦並びを切り替える。
    private async void OnToggleEditorSupportSlideMode(object sender, RoutedEventArgs e)
    {
        _settings.Appearance.MarkdownSlideMode = EditorSupportSlideToggle.IsChecked == true;
        await UpdateEditorSupportAsync();
    }

    // EditorSupport ヘッダーの「ブラウザで開く」：現在のプレビューを Loomo 内蔵ブラウザの新規タブへ スナップショットとして開く（以降の編集には追従しない一回きりの表示）。URI 提供者（PDF 等）は そのファイルを直接開き、HTML 提供者（Markdown/JSON プレビュー等）は現在の本文から HTML を 再生成し、画像・アセット用の仮想ホストをそのタブの CoreWebView2 にも張ってから開く （EditorSupport ペイン自身のマップはそのペインの CoreWebView2 専用で、他のタブへは及ばないため）。
    private async void OnOpenEditorSupportInBrowser(object sender, RoutedEventArgs e)
    {
        var source = _editorSupport.Source;
        var filePath = source?.Control.FilePath;
        if (source is null || filePath is null)
            return;

        var provider = _editorSupports.Resolve(filePath);
        if (provider is IEditorSupportUriProvider uriProvider)
        {
            await OpenUrlInBrowserAsync(uriProvider.ResolveNavigationUri(filePath), uriProvider.DescribeTitle(filePath));
            return;
        }

        if (provider is not IEditorSupportHtmlProvider htmlProvider)
            return; // ビジュアル提供者（CSV/TSV グリッド等）や対応の無いファイルは開ける HTML が無い。

        var title = htmlProvider.DescribeTitle(filePath);
        var mapFolder = MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).MapFolder;
        var htmlText = htmlProvider.UsesEditorText ? source.Control.Text : string.Empty;
        var html = await Task.Run(() => htmlProvider.RenderHtml(filePath, htmlText));

        await OpenEditorSupportSnapshotInBrowserAsync(html, mapFolder, title);
    }

    private void UpdateEditorSupportPinToggle()
    {
        EditorSupportPinToggle.IsChecked = _editorSupport.IsPinned;
        EditorSupportPinToggle.ToolTip = _editorSupport.IsPinned
            ? "ピン留めを解除してアクティブなエディタに追従"
            : "現在のサポート対象にピン留め";
    }

    // EditorSupport ヘッダーの Web 系ボタン（発表モード／ブラウザで開く／エクスポート）の表示・非表示。 ピン留めはどの表示種別にも意味があるので常時表示のまま、この3つだけを現在の表示内容 （HTML／URI／ビジュアル／コードアウトライン）に応じて絞る（例：コード構造や画像・Hex・CSV グリッド 表示中はブラウザで開く先や発表モードが無いので隠す）。
    private void UpdateEditorSupportHeaderButtons(bool showSlide, bool showOpenInBrowser, bool showExport)
    {
        EditorSupportSlideToggle.Visibility = showSlide ? Visibility.Visible : Visibility.Collapsed;
        EditorSupportOpenInBrowserButton.Visibility = showOpenInBrowser ? Visibility.Visible : Visibility.Collapsed;
        EditorSupportExportButton.Visibility = showExport ? Visibility.Visible : Visibility.Collapsed;
    }

    // 編集中の連続更新をまとめる（300ms デバウンスで UpdateEditorSupportAsync）。
    private void ScheduleEditorSupportUpdate()
    {
        if (_editorSupport.Source is null)
            return;

        if (_editorSupportDebounceTimer is null)
        {
            _editorSupportDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _editorSupportDebounceTimer.Tick += async (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                await UpdateEditorSupportAsync();
            };
        }

        _editorSupportDebounceTimer.Stop();
        _editorSupportDebounceTimer.Start();
    }

    // 追従先エディタの内容を EditorSupport ペインへ反映する。ペインの開閉はしない（明示操作のみ）。 ペインが表示されている（タイルで可視 or ソロで舞台）ときだけ中身を描く。
    private async Task UpdateEditorSupportAsync()
    {
        var source = _editorSupport.Source;
        if (source is null)
            return;

        var filePath = source.Control.FilePath;
        var provider = _editorSupports.Resolve(filePath);

        // 自動表示はしない。ペインが実際に表示されている （タイルで可視 / ソロで舞台 / 袖・俯瞰ミニチュア）ときだけ描く。 判定は EditorSupportRenderPolicy に一元化（テスト可能）。
        var onStage = _stageActive && _stagePane == PaneKind.EditorSupport;
        if (!EditorSupportRenderPolicy.ShouldRender(
                onStage,
                IsPaneVisible(PaneKind.EditorSupport),
                IsEditorSupportInThumbnail()))
            return;

        // 専用プロバイダの無いコードファイルは、LSP ベースの構造アウトラインへフォールバックする （registry 外・Hex と同じ形）。専用 provider 解決の後・ビジュアル/Hex 判定より前で拾う。
        if (provider is null && filePath is not null && _codeSupport.CanHandle(filePath))
        {
            UpdateEditorSupportHeaderButtons(showSlide: false, showOpenInBrowser: false, showExport: false);
            await UpdateCodeEditorSupportAsync(source, filePath);
            return;
        }

        // WPF コントロールをそのまま表示する提供者（CSV/TSV グリッド等）。WebView2 は使わない。 対応プロバイダが無く、かつバイナリのファイルは Hex ダンプへフォールバックする（registry 外）。
        var visual = provider as IEditorSupportVisualProvider;
        if (visual is null && provider is null && filePath is not null && BinaryFileDetector.IsBinary(filePath))
            visual = _hexSupport;

        if (visual is not null && filePath is not null)
        {
            if (_editorSupportEditSubscribed.Add(visual))
                visual.ContentEdited += EditorSupportVisual_ContentEdited;

            UpdateEditorSupportHeaderButtons(showSlide: false, showOpenInBrowser: false, showExport: false);
            ShowEditorSupportVisual(visual.GetOrCreateView());
            EditorSupportTitle.Text = visual.DescribeTitle(filePath);
            // ファイル直読み系（Image/Hex/Office 等）はエディタ本文を使わない。巨大バイナリを文字列化して 渡す無駄を避け、UsesEditorText の提供者にだけ Control.Text を渡す。
            await visual.UpdateAsync(filePath, visual.UsesEditorText ? source.Control.Text : string.Empty);
            return;
        }

        // WebView2 系。直前までビジュアル系を表示していたら退ける。
        HideEditorSupportVisual();

        // 本文スナップショットは UI スレッドで取る（エディタは UI スレッド専有）。重い Markdown→HTML 変換はこの後バックグラウンドで行うので、ここで一度だけ読む。 ファイル直読み系（Office 等 UsesEditorText=false）は本文を使わないので取得しない。
        var text = (provider?.UsesEditorText ?? true) ? source.Control.Text : string.Empty;

        // 描画要求のシーケンス番号。init / 変換の await を跨いで最後の要求だけが描くよう畳む。
        var seq = ++_editorSupportRenderSeq;

        string title;
        string? html = null;
        string? body = null;
        string? uri = null;
        string? mapFolder = null;
        string? pageKey = null;
        if (provider is IEditorSupportUriProvider uriProvider && filePath is not null)
        {
            // PDF・SVG・HTML 等はファイルをそのままブラウザへナビゲートする（本文には依存しない）。
            UpdateEditorSupportHeaderButtons(showSlide: false, showOpenInBrowser: true, showExport: false);
            title = uriProvider.DescribeTitle(filePath);
            uri = uriProvider.ResolveNavigationUri(filePath);
        }
        else if (provider is IEditorSupportHtmlProvider htmlProvider && filePath is not null)
        {
            // 発表モードは marp 文書（Markdown）にしか効かないので、Markdown 提供者のときだけ出す。
            UpdateEditorSupportHeaderButtons(
                showSlide: provider is MarkdownEditorSupport, showOpenInBrowser: true, showExport: true);
            title = htmlProvider.DescribeTitle(filePath);
            mapFolder = MarkdownPreviewPaths.Resolve(_workspace.RootPath, filePath).MapFolder;

            // 同一ページ（テーマ・base href・対象ファイルが不変）を編集中なら、本文だけ差し替えて フル再ナビゲート（＝ページ再構築のチカチカ）を避ける。鍵が変わったら従来どおり再構築する。
            var incremental = htmlProvider as IEditorSupportIncrementalHtmlProvider;
            pageKey = incremental?.PageContextKey(filePath, text);
            var reuseLoadedPage = incremental is not null && pageKey == _editorSupportWebView.ReadyPageKey;

            // Markdown→HTML 変換は正規表現主体で重く、大きいファイルでは打鍵を固める。バックグラウンド スレッドで変換し、結果だけを UI スレッドへ戻して反映する（ユーザー操作を妨げない）。 変換例外はここで受け止める：このメソッドは ActivateEditorTab 等から fire-and-forget （`_ = SwitchEditorSupportSourceAsync(...)`）で呼ばれるため、投げっぱなしにすると タスクが黙って死に、ペインが直前の内容（別タブ・別ワークスペースのものすら）に 固まったまま以降の切替・編集でも一切更新されなくなる。
            try
            {
                if (reuseLoadedPage)
                    body = await Task.Run(() => incremental!.RenderBody(filePath, text));
                else
                    html = await Task.Run(() => htmlProvider.RenderHtml(filePath, text));
            }
            catch (Exception ex)
            {
                body = null;
                pageKey = null; // 壊れたページを以降の本文差し替え判定の「同一鍵」に使わせない
                html = MarkdownRenderer.RenderToHtml(
                    $"## プレビューエラー\n\n変換中に例外が発生しました。\n\n```\n{ex}\n```",
                    title,
                    _settings.Appearance.MarkdownPreviewTheme);
            }

            // 変換中に新しい要求が来ていれば、そちらが最新を描くのでこのコールは降りる。
            if (seq != _editorSupportRenderSeq)
                return;
        }
        else
        {
            // 手動表示中で対応プロバイダの無いファイル：案内だけ出す。
            UpdateEditorSupportHeaderButtons(showSlide: false, showOpenInBrowser: false, showExport: false);
            title = "Editor Support";
            html = MarkdownRenderer.RenderToHtml(
                "## Editor Support\n\nこのファイルに対応するサポートはありません。",
                title,
                _settings.Appearance.MarkdownPreviewTheme);
        }

        _editorSupportWebView.SetPending(html, body, uri, mapFolder, pageKey);
        EditorSupportTitle.Text = title;

        var view = await EnsureEditorSupportViewAsync();
        if (view?.CoreWebView2 is null)
            return;

        // init を待っている間に新しい描画要求が来ていれば、そちらが描くのでこのコールは降りる （起動時に殺到した要求が同時に NavigateToString して初回ナビゲーションを潰し合うのを防ぐ）。
        if (seq != _editorSupportRenderSeq)
            return;

        RenderPendingEditorSupportContent(view.CoreWebView2);
    }

    // 専用プロバイダの無いコードファイルに対し、LSP のドキュメントシンボルから構造アウトラインと ②呼び出し解析を EditorSupport ペインへ描く（フォールバック）。表示はネイティブ WPF （CodeOutlineView）で、WebView2 は経由しない（初回コールドスタート・白フラッシュ回避）。 言語サーバー未接続／未対応なら同ビューの案内状態を出す。await を跨ぐ古い要求は _editorSupportRenderSeq で畳む。
    private async Task UpdateCodeEditorSupportAsync(EditorTab source, string filePath, bool fromReadyRetry = false)
    {
        // 描画要求のシーケンス番号。LSP 呼び出しの await を跨いで最後の要求だけが描くよう畳む。
        var seq = ++_editorSupportRenderSeq;
        var lsp = GetLspManager(source);

        // 接続済み・ドキュメント準備済みに加え、LSP の現在ドキュメントが対象ファイルと一致することも 条件に含める（別ファイルのアウトラインを誤って描かないため）。
        var ready = lsp is not null && lsp.IsConnected && lsp.IsDocumentReady && LspMatchesFile(lsp, filePath);

        // 診断：コード表示要求の入口。ready 待ちのポーリング tick からの再入（fromReadyRetry）では計り直さず、 新規要求（ファイル切替・オープン・編集）のときだけ「ユーザーが待ち始めた」起点として計り直す。
        if (CodeSupportDiag.IsEnabled)
        {
            if (!fromReadyRetry)
                _codeSupportDiagStopwatch = System.Diagnostics.Stopwatch.StartNew();
            CodeSupportDiag.Log(
                $"enter file={Path.GetFileName(filePath)} ready={ready} " +
                $"lsp={(lsp is null ? "null" : "ok")} connected={lsp?.IsConnected} docReady={lsp?.IsDocumentReady} " +
                $"match={(lsp is not null && LspMatchesFile(lsp, filePath))} " +
                $"elapsed={_codeSupportDiagStopwatch?.ElapsedMilliseconds ?? 0}ms retryTick={_codeReadyRetryAttempts}");
        }

        // コードビュー（無ければ生成）。ペインへの差し替え自体は「実際に何か描くとき」まで遅延する （↓の grace 参照）。差し替え＋タイトル更新をまとめる小ヘルパ。
        var view = EnsureCodeOutlineView();
        void ShowCodeView()
        {
            ShowEditorSupportVisual(view);
            EditorSupportTitle.Text = _codeSupport.DescribeTitle(filePath);
        }

        if (!ready)
        {
            // 追従キャッシュは破棄（キャレット追従を止める）。案内を出したまま ready へ遷移しても再描画 契機が無いので、ready になるまで軽くポーリングして本描画へ差し替える（案内は描き直さない）。
            _codeOutlineRoots = null;
            _codeOutlineSource = null;
            _codeCurrentSymbolRange = null;
            _codeCurrentCaret = null;

            // ファイル切替直後、サーバーが ready になるまでの短い待ち（warm でも約1秒）に「接続待ち」案内を 毎回フラッシュさせるとうるさい、というフィードバックへの対応。導入済みで接続待ちのとき （EvaluateForFile==null）は grace 経過まで案内もペイン差し替えもせず、前の表示を保つ＝ ready が grace 内に来れば案内は一切出ずに構造へ直行する。サーバー未導入（!=null）は actionable かつ永続的なので従来どおり即出す（フラッシュ対象ではない）。
            var prompt = _lspManagement.EvaluateForFile(filePath);
            if (prompt is not null || _codeReadyRetryAttempts >= CodeConnectingNoticeGraceTicks)
            {
                ShowCodeView();
                view.ShowNotice(LspNoticeModel.Build(prompt));
            }
            ScheduleCodeReadyRetry();
            return;
        }

        // ready＝これから構造を描く。ここでペインをコードビューへ差し替える（grace 中は差し替えていない）。
        ShowCodeView();

        // ready に到達＝アウトラインを描く。接続待ちポーリングは役目を終えたので止める（以降のコールド構造 リトライはこのメソッド内のループで面倒を見る）。
        StopCodeReadyRetry();
        // 診断：ready 待ちの合計（＝この地点までの経過）。ここから先は LSP 往復のコスト。
        CodeSupportDiag.Log($"ready reached after {_codeSupportDiagStopwatch?.ElapsedMilliseconds ?? 0}ms");

        var symbolsSw = CodeSupportDiag.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        var symbols = await RequestDocumentSymbolsSafeAsync(lsp!);
        CodeSupportDiag.Log($"documentSymbols {symbolsSw?.ElapsedMilliseconds ?? 0}ms count={symbols.Count}");

        // 取得の await を跨いで新しい要求が来ていれば、そちらが描くのでこのコールは降りる。
        if (seq != _editorSupportRenderSeq)
            return;

        // Caret.Line/Column は 0 始まり。LSP の DocumentSymbol も 0 始まりなので current 判定はそのまま、 表示・ジャンプ用の行だけ +1 して 1 始まりにする（ビュー側 DataLine1）。
        var caret = source.Control.Caret;
        // シグネチャ（Detail）は本文の宣言行から切り出すので、ここで一度だけ行分割して渡す （エディタは UI スレッド専有。以降の LSP await を跨がないよう先に取る）。
        var roots = CodeEditorSupport.ToOutline(symbols, SplitLines(source.Control.Text));

        // ★構造アウトラインを②（呼び出し解析）から切り離す。②の LSP 往復（特に prepareCallHierarchy は コールド初回でプロジェクトのロード待ちに数秒かかる）に構造描画を引きずらせない＝ documentSymbols が返った時点で構造を先に描き、②はこの後バックグラウンドで後埋めする。 ただし構造が空（コールド初回はプロジェクト未ロードで documentSymbols が空を返す）のときは 誤った空アウトラインを確定させず、案内のまま②往復でサーバーを温めてから構造を取り直す（下記）。
        if (roots.Count > 0)
        {
            var currentLine1 = CurrentMemberLine1(roots, caret);
            _codeOutlineRoots = roots;
            _codeOutlineSource = source;
            _codeCurrentSymbolRange = null;                       // ②は未取得（この後 SetCurrentAndPanels で埋める）
            _codeCurrentCaret = (caret.Line, caret.Column);
            view.ShowOutline(roots, currentLine1, CallPanels.Empty);   // 構造だけ先に（②は待たない）
            LogOutlineShown("structure");
        }
        else
        {
            // コールド初回：プロジェクト未ロードで構造が空。②往復でサーバーを温める数秒間、誤解を招く 「（コード構造がありません）」プレースホルダではなく接続待ち案内を出しておく（真に数秒かかる 待ちなので案内が適切。warm の一瞬の待ちは grace 側で既に抑制済み）。
            view.ShowNotice(LspNoticeModel.Build(null));
        }

        // ②（呼び出し元/先・使用箇所）は<b>キャレット直下のシンボル</b>で問い合わせる（IDE の「参照を検索」相当）。 返る名前範囲はキャレット追従の差分基準（このシンボル上を動く間は再取得しない）に使う。 コールド初回はここでサーバーが温まる（副作用）。
        var panelsSw = CodeSupportDiag.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        var (panels, symbolRange) = await FetchCallPanelsAsync(lsp!, caret.Line, caret.Column);
        CodeSupportDiag.Log(
            $"callPanels {panelsSw?.ElapsedMilliseconds ?? 0}ms " +
            $"in={panels.Incoming.Count} out={panels.Outgoing.Count} refs={panels.References.Count}");
        if (seq != _editorSupportRenderSeq)
            return;

        if (roots.Count > 0)
        {
            // 構造は既に描画済み → ②パネルだけ後埋め（ツリー非再構築＝折りたたみ・スクロール保持）。
            var currentLine1 = CurrentMemberLine1(roots, caret);
            _codeCurrentSymbolRange = symbolRange;
            _codeCurrentCaret = (caret.Line, caret.Column);
            view.SetCurrentAndPanels(currentLine1, panels);
            LogOutlineShown("panels");
            return;
        }

        // ここに来る＝構造が空だった（コールド初回のプロジェクト未ロードが濃厚）。②往復でサーバーが 温まったはずなので、構造を短くリトライして取り直す（②は再取得しない）。取れたら確定描画、 上限まで空のまま（＝本当に空のファイル等）なら空アウトライン＋取れた②で確定する。
        for (var attempt = 0; attempt < CodeColdStructureRetries; attempt++)
        {
            symbols = await RequestDocumentSymbolsSafeAsync(lsp!);
            if (seq != _editorSupportRenderSeq)
                return;
            roots = CodeEditorSupport.ToOutline(symbols, SplitLines(source.Control.Text));
            if (roots.Count > 0)
                break;
            await Task.Delay(CodeColdStructureRetryDelay);
            if (seq != _editorSupportRenderSeq)
                return;
        }
        CodeSupportDiag.Log($"cold structure refetch count={roots.Count}");

        var current = CurrentMemberLine1(roots, caret);
        _codeOutlineRoots = roots;
        _codeOutlineSource = source;
        _codeCurrentSymbolRange = symbolRange;
        _codeCurrentCaret = (caret.Line, caret.Column);
        view.ShowOutline(roots, current, panels);
        LogOutlineShown(roots.Count > 0 ? "cold-structure+panels" : "empty");
    }

    // コールド初回（プロジェクト未ロードで空）のとき、②往復後に構造を取り直すリトライ回数。
    private const int CodeColdStructureRetries = 6;

    // コールド構造リトライの間隔（②往復でサーバーは温まっている前提の軽いリトライ）。
    private static readonly TimeSpan CodeColdStructureRetryDelay = TimeSpan.FromMilliseconds(300);

    // ドキュメントシンボルを取得する（失敗しても落とさず空で返す）。
    private static async Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsSafeAsync(IEditorLspManager lsp)
        => await CodeEditorSupportAnalysis.RequestDocumentSymbolsSafeAsync(lsp);

    // アウトラインの current ハイライト行（1 始まり・0＝無し）：キャレットを含む最深メンバー。
    private static int CurrentMemberLine1(IReadOnlyList<OutlineNode> roots, CaretInfo caret)
        => CodeEditorSupportAnalysis.CurrentMemberLine1(roots, caret);

    // 診断：入口（ユーザーが待ち始めた地点）から結果が見えるまでの合計を記録する（構造描画時／②後埋め時）。
    private void LogOutlineShown(string phase)
    {
        if (_codeSupportDiagStopwatch is not null)
            CodeSupportDiag.Log($"shown[{phase}], TOTAL {_codeSupportDiagStopwatch.ElapsedMilliseconds}ms");
    }

    // コード構造アウトラインの WPF ビューを（無ければ作って）返す。初回にジャンプ／インストール等の 操作イベントを既存導線へ配線する（以降は使い回すので一度だけ）。
    private CodeOutlineView EnsureCodeOutlineView()
    {
        if (_codeOutlineView is not null)
            return _codeOutlineView;

        var view = new CodeOutlineView();
        // アウトラインのメンバー名クリック → ソース行（1 始まり）へジャンプしてフォーカスを戻す。 コードジャンプは対象行を vim の zt 相当でビュー最上段へ寄せる（alignTop: true）。
        view.SourceLineActivated += (_, line1) => FocusEditorSupportSource(line1 > 0 ? line1 : null, alignTop: true);
        // ②パネルの行クリック → 別ファイル（または同一ファイル）の該当行を開く（1 始まり→内部で 0 始まりへ）。
        view.FileLocationActivated += (_, e) => _ = OpenPathInEditorAsync(e.Path, e.Line1, column: 0, alignTop: true);
        // 案内ページのボタン → 既存導線を再利用する。
        view.InstallRequested += (_, _) => InstallLspForEditorSupportSource();
        view.OpenLspSettingsRequested += (_, _) => _vm.LspPrompt.OpenSettingsCommand.Execute(null);
        view.OpenDocsRequested += (_, url) => _ = OpenUrlInBrowserAsync(url, null);

        _codeOutlineView = view;
        return view;
    }

    // LSP の現在ドキュメント（IEditorLspManager.CurrentUri・file:// URI）が対象 filePath と同一ファイルを指しているか。file URI をローカルパス化して大小無視で比較する。
    private static bool LspMatchesFile(IEditorLspManager lsp, string filePath)
        => CodeEditorSupportAnalysis.LspMatchesFile(lsp, filePath);

    // ②呼び出し解析を LSP から取得する。PrepareCallHierarchyAsync → 呼び出し元/先、 RequestReferencesAsync → 使用箇所。キャレット直下のシンボル（line0/col0、 0 始まり）で問い合わせる＝IDE の「参照を検索」相当。callHierarchy 非対応サーバーやシンボル外の位置でも 例外を投げず、取れた分だけ（無ければ空で）返す。あわせて解決できたシンボルの名前範囲 （callHierarchy の SelectionRange）を返し、呼び出し側のキャレット追従の差分基準に使う （このシンボル上を動く間は再取得しない）。解決できなければ範囲は null。 LSP 往復は互いに独立なものを並列化して初回結果の到達を縮める：使用箇所（references）は callHierarchy に依存しないので即開始し、呼び出し元/先（incoming/outgoing）は prepare 解決後に 2 本同時に投げる（各サーバーは複数リクエストを多重化できる）。
    private static Task<(CallPanels Panels, LspRange? SymbolRange)> FetchCallPanelsAsync(
        IEditorLspManager lsp, int line0, int col0)
        => CodeEditorSupportAnalysis.FetchCallPanelsAsync(lsp, line0, col0);

    // 本文をシグネチャ抽出用に行分割する（改行種別を吸収。0 始まり index が LSP の line と一致）。
    private static IReadOnlyList<string> SplitLines(string? text)
        => CodeEditorSupportAnalysis.SplitLines(text);

    // キャレット（0 始まり line/col）が LSP 範囲 range（0 始まり・両端含む）の内側か。 ②の差分基準：直近に解決したシンボルの名前範囲にキャレットが留まる間は同じシンボル＝再取得しない。
    private static bool CaretInRange(LspRange range, int line0, int col0)
        => CodeEditorSupportAnalysis.CaretInRange(range, line0, col0);

    // キャレット追従（②の再取得）。ScheduleCodeCallPanelsRefresh のデバウンス満了で呼ばれる。 ②はキャレット直下のシンボルで問い合わせる（IDE の「参照を検索」相当）。直近に解決したシンボルの 名前範囲（_codeCurrentSymbolRange）にキャレットが留まる間は同じシンボル＝再取得しない。 範囲が取れなかった（変数・空白上等）ときはキャレット位置（_codeCurrentCaret）で差分を取る。 アウトライン（＝ドキュメントシンボル）は取り直さない（構造は編集でしか変わらない）。
    private async Task RefreshCodeCallPanelsAsync()
    {
        var source = _editorSupport.Source;
        if (source is null)
            return;

        // コードページを描いていない／別タブへ移った＝追従対象外。
        var roots = _codeOutlineRoots;
        if (roots is null || !ReferenceEquals(_codeOutlineSource, source))
            return;

        // ペインが見えていなければ描かない（通常更新と同じゲート）。
        var onStage = _stageActive && _stagePane == PaneKind.EditorSupport;
        if (!EditorSupportRenderPolicy.ShouldRender(
                onStage, IsPaneVisible(PaneKind.EditorSupport), IsEditorSupportInThumbnail()))
            return;

        var caret = source.Control.Caret;

        // 同じシンボル上（前回解決した名前範囲の内側）の移動、または範囲が取れなかった前回と同一キャレット位置の ままなら②を再取得しない。※キャッシュ更新は実描画の直前まで遅延する（await 後に降りたら旧値で再試行）。
        if (_codeCurrentSymbolRange is { } range && CaretInRange(range, caret.Line, caret.Column))
            return;
        if (_codeCurrentSymbolRange is null && _codeCurrentCaret is { } prev
            && prev.Line == caret.Line && prev.Col == caret.Column)
            return;

        var filePath = source.Control.FilePath;
        if (filePath is null)
            return;
        var lsp = GetLspManager(source);
        if (lsp is null || !lsp.IsConnected || !lsp.IsDocumentReady || !LspMatchesFile(lsp, filePath))
            return;

        var seq = ++_editorSupportRenderSeq;
        // ②はキャレット直下のシンボルで問い合わせる（解決できなければ空パネル＋範囲 null）。
        var (panels, symbolRange) = await FetchCallPanelsAsync(lsp, caret.Line, caret.Column);
        if (seq != _editorSupportRenderSeq)
            return;

        // アウトラインの current ハイライトはキャレットを含む最深メンバーへ付ける。
        var member = CodeOutline.FindEnclosing(roots, caret.Line, caret.Column);
        var currentLine1 = member is null ? 0 : member.Line0 + 1; // current を付け替える行（1 始まり）

        // ツリーは作り直さず current 付替え＋②パネル差し替えだけ（折りたたみ状態・スクロールを保つ）。
        _codeCurrentSymbolRange = symbolRange;
        _codeCurrentCaret = (caret.Line, caret.Column);
        _codeOutlineView?.SetCurrentAndPanels(currentLine1, panels);
    }

    // コード案内ページの「インストール」導線。現在の追従元ファイルの拡張子から対応サーバーを再判定し、 可視ターミナルでインストールコマンドを実行する（端末未接続や導入済みなら何もしない）。
    private void InstallLspForEditorSupportSource()
    {
        var filePath = _editorSupport.Source?.Control.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return;

        var info = _lspManagement.EvaluateForFile(filePath);
        if (info is null)
            return; // 既に導入済み等（案内ボタンが古い）：何もしない

        // 実行結果（可視ターミナルの有無）に関わらず、案内ページ自体はそのまま（LSP 接続後の 再オープン／再描画で自然にアウトラインへ切り替わる）。
        _lspManagement.InstallForPrompt(info);
    }

    // 案内表示中に ready を待つポーリングの間隔。短くして「ファイル切替→結果表示」の待ちを詰める。
    private static readonly TimeSpan CodeReadyRetryInterval = TimeSpan.FromMilliseconds(200);

    // ready を待つポーリングの最大試行回数（CodeReadyRetryInterval 間隔で約 25 秒に相当）。 サーバーが永久に来ないケースの保険。
    private const int CodeReadyMaxRetries = 125;

    // 「接続待ち」案内（導入済みサーバーの ready 待ち）を出すまでの grace（CodeReadyRetryInterval 間隔の tick 数≒1.6 秒）。ファイル切替直後の warm な ready 待ち（実測 ~0.8-1.1 秒）にいちいち案内を フラッシュさせないための猶予。この間に ready へ遷移すれば案内は一切出ず構造へ直行する。 未導入サーバー（actionable な案内）は grace 対象外で即出す。
    private const int CodeConnectingNoticeGraceTicks = 8;

    // 案内（言語サーバー接続待ち）を出したあと、ready へ遷移したかを CodeReadyRetryInterval 間隔で 確認するポーリングを開始する。既に動いていれば何もしない。ready でない間は案内を描き直さない （チカチカ防止）＝ready になった tick でだけ本描画（UpdateCodeEditorSupportAsync の ready 分岐）へ差し替える。
    private void ScheduleCodeReadyRetry()
    {
        if (_codeReadyRetryTimer is null)
        {
            _codeReadyRetryTimer = new DispatcherTimer { Interval = CodeReadyRetryInterval };
            _codeReadyRetryTimer.Tick += CodeReadyRetry_Tick;
        }

        if (!_codeReadyRetryTimer.IsEnabled)
        {
            _codeReadyRetryAttempts = 0;
            _codeReadyRetryTimer.Start();
        }
    }

    // 接続待ちポーリングを止める（ready 到達・対象変更・上限で呼ぶ）。
    private void StopCodeReadyRetry()
    {
        _codeReadyRetryTimer?.Stop();
        _codeReadyRetryAttempts = 0;
    }

    private async void CodeReadyRetry_Tick(object? sender, EventArgs e)
    {
        var source = _editorSupport.Source;
        var filePath = source?.Control.FilePath;

        // 対象が変わった／コード外／既にアウトライン描画済み → 停止。
        if (source is null || filePath is null || !_codeSupport.CanHandle(filePath) || _codeOutlineRoots is not null)
        {
            StopCodeReadyRetry();
            return;
        }

        if (++_codeReadyRetryAttempts > CodeReadyMaxRetries)
        {
            StopCodeReadyRetry(); // サーバーが来ない：案内のまま諦める（ペイン再オープンで再試行される）
            return;
        }

        // 非表示中は描かない（が、次 tick で再確認する＝ポーリングは継続）。
        var onStage = _stageActive && _stagePane == PaneKind.EditorSupport;
        if (!EditorSupportRenderPolicy.ShouldRender(
                onStage, IsPaneVisible(PaneKind.EditorSupport), IsEditorSupportInThumbnail()))
            return;

        var lsp = GetLspManager(source);
        var ready = lsp is not null && lsp.IsConnected && lsp.IsDocumentReady && LspMatchesFile(lsp, filePath);
        if (!ready)
        {
            // grace を過ぎてもまだ ready でない＝一瞬で消える待ちではなくサーバーが遅い/来ない。ここで初めて 「接続待ち」案内へ差し替える（本メソッドの not-ready 分岐が attempts>=grace で案内を出す）。 ちょうど grace 到達の tick でのみ一度呼ぶ（以降の tick は案内を描き直さずポーリングだけ）。
            if (_codeReadyRetryAttempts == CodeConnectingNoticeGraceTicks)
                await UpdateCodeEditorSupportAsync(source, filePath, fromReadyRetry: true);
            return;
        }

        // ready 到達：本描画へ差し替える（ready 分岐が StopCodeReadyRetry する）。 fromReadyRetry=true＝ポーリング中の再入。診断ストップウォッチを計り直さない（待ちの起点を保つ）。
        await UpdateCodeEditorSupportAsync(source, filePath, fromReadyRetry: true);
    }

    // キャレット追従（②再取得）を 150ms デバウンスする。コードページ未描画のときは無視。
    private void ScheduleCodeCallPanelsRefresh()
    {
        // コードページを描いていなければ追従不要（非コードファイル・案内表示中など）。
        if (_codeOutlineRoots is null)
            return;

        if (_codeCaretTimer is null)
        {
            _codeCaretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _codeCaretTimer.Tick += async (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                await RefreshCodeCallPanelsAsync();
            };
        }

        _codeCaretTimer.Stop();
        _codeCaretTimer.Start();
    }

    // 追従元エディタのキャレット移動：コード解析（②）をデバウンスして追従する。
    private void EditorSupportSource_CaretMoved(object? sender, CaretInfo e)
        => ScheduleCodeCallPanelsRefresh();

    // Markdown プレビューでタスクリストのチェックボックスをクリックしたときの反映。 対応するソース行（0始まり、プレビュー生成時にフロントマター分ずらして埋め込んだもの）の
    // [ ]/[x] を反転してエディタの本文を書き換える。Vim のモード（挿入中など）に依存 せず安全に書き換えられるよう、キー入力を経由しない VimEditorControl.SetText を使う （プレビュー側のクリックはエディタの現在モードと無関係に届くため、ex コマンド経由だと挿入モード中に コロンがそのまま入力されてしまう）。
    private void ToggleMarkdownTaskCheckbox(int lineIndex)
    {
        var source = _editorSupport.Source;
        if (source is null)
            return;

        var text = source.Control.Text;
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length)
            return;

        var toggled = MarkdownRenderer.ToggleTaskListLine(lines[lineIndex]);
        if (toggled is null)
            return;

        lines[lineIndex] = toggled;
        // SetText はエディタのビューポートを組み直すため ViewportScrolled が発火する。ここで流れる エディタの行ベース比率をプレビューへ送るとピクセル位置へ変換し直されてスクロールが微妙にズレる （チェックボックスの反転は文書高を変えないので本来動くべきではない）。同期エコーを抑止する。
        _syncingEditorFromSupport = true;
        try { source.Control.SetText(string.Join(eol, lines)); }
        finally { _syncingEditorFromSupport = false; }
        ScheduleEditorSupportUpdate();
    }

    // EditorSupport が袖または俯瞰のミニチュアとして表示される状態か。 Main に自動表示はしないが、VisualBrush の描画元には中身が必要なので描画だけ許可する。
    private bool IsEditorSupportInThumbnail()
    {
        if (!IsSessionEnabled(PaneKind.EditorSupport))
            return false;

        if (_stageActive)
            return _overviewActive || _stagePane != PaneKind.EditorSupport;

        return !IsShownInMain(PaneKind.EditorSupport);
    }
}
