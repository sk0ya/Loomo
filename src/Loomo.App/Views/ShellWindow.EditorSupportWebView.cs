
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: EditorSupport ペイン（Markdown プレビュー等の表示・スクロール同期）。

/// <summary>ShellWindow: EditorSupport ペインの WebView2 レンダリングとライフサイクル（HTML/本文/URI の
/// 反映、一時ページ書き出し、ビジュアル提供者の差し替え、CoreWebView2 の遅延生成・初期化、スクロール同期）。
/// 追従元タブの管理と更新オーケストレーションは ShellWindow.EditorSupport.cs。</summary>
public partial class ShellWindow
{
    /// <summary>
    /// 最新の描画内容を WebView2 へ反映する。URI プロバイダ（PDF 等）はファイルへ直接ナビゲートし、
    /// それ以外（Markdown プレビュー等）は HTML 文字列をナビゲートする。
    /// </summary>
    private void RenderPendingEditorSupportContent(CoreWebView2 core)
    {
        if (_editorSupportPendingUri is { } uri)
        {
            // 同一ファイルへの再ナビゲート（本文編集のデバウンス等）は PDF のスクロール位置を失うので避ける。
            if (string.Equals(uri, _editorSupportNavigatedUri, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                core.Navigate(uri);
                _editorSupportNavigatedUri = uri;
                _editorSupportReadyPageKey = null; // 別ページへ移った：次の Markdown はフル再構築する
            }
            catch
            {
                // 無効な URI 等で失敗しても落とさない（表示は前回内容のまま）。
            }
            return;
        }

        // 同一ページの本文だけ差し替える（フル再ナビゲートしない＝チカチカ・スクロール喪失なし）。
        // ページ側スクリプトが document.body.innerHTML を入れ替え、mermaid を描き直す。
        if (_editorSupportPendingBody is { } body)
        {
            // 同じファイルでも別フォルダへ移った場合に備えて画像のマップ先は更新しておく。
            if (_editorSupportPendingMapFolder is not null)
                _editorSupportNavigation.UpdatePreviewHost(core, _editorSupportPendingMapFolder);

            try
            {
                core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(
                    new { type = "setBody", html = body }));
            }
            catch
            {
                // 送信失敗（ナビゲーション中等）でも落とさない（次の編集で再送される）。
            }
            return;
        }

        if (_editorSupportPendingHtml is null)
            return;

        // HTML を描いたら、次に同じ URI へ戻ったとき確実に再ナビゲートできるようガードを解除する。
        _editorSupportNavigatedUri = null;

        if (_editorSupportPendingMapFolder is not null)
            _editorSupportNavigation.UpdatePreviewHost(core, _editorSupportPendingMapFolder);

        // 新ページの読込が完了するまで本文差し替えは受け付けられない（ready 鍵を一旦クリアし、
        // 読込中の鍵を控える）。NavigationCompleted で ready へ昇格させ、そこから setBody を許す。
        _editorSupportReadyPageKey = null;
        _editorSupportLoadingPageKey = _editorSupportPendingPageKey;

        // NavigateToString は約 2MB が上限で大きな Markdown を取りこぼし、初回ナビゲーションが完了しない
        // ために setBody の差分更新も始まらない（＝大きいファイルが一切表示・更新されない）。生成済み HTML を
        // 一時ファイルへ書き出して page.loomo 経由でナビゲートすればサイズ無制限になる。書き出し失敗時のみ
        // 従来の NavigateToString へ退避する。
        if (_editorSupportNavigation.TryWritePage(_editorSupportPendingHtml, out var pageUrl))
        {
            try
            {
                core.Navigate(pageUrl);
            }
            catch
            {
                // ナビゲート失敗でも落とさない（プレビューは前回内容のまま）。
                _editorSupportLoadingPageKey = null;
            }
            return;
        }

        try
        {
            core.NavigateToString(_editorSupportPendingHtml);
        }
        catch
        {
            // NavigateToString の上限（約2MB）超過などで失敗しても落とさない（プレビューは前回内容のまま）。
            _editorSupportLoadingPageKey = null;
        }
    }

    /// <summary>
    /// プレビューページの HTML を一時ファイルへ書き出し、page.loomo 経由のナビゲート URL を返す。
    /// <c>?v=</c> に毎回違う版番号を載せることで同一ファイルでも新 URL になり、WebView2 のキャッシュで
    /// 古いプレビューが居座らないようにする。書き出し失敗（権限・IO 等）時は false。
    /// </summary>
    /// <summary>
    /// URL が EditorSupport の「ブラウザで開く」が書き出した一時プレビューページ（<see cref="MarkdownRenderer.PageVirtualHost"/>）
    /// を指しているか。ワークスペース保存時にこの手のタブを除外する判定に使う。
    /// </summary>
    /// <summary>
    /// プレビュー HTML を一時ファイルへ書き出し、新規ブラウザタブでその仮想ホストを張ってから開く
    /// （<see cref="OnOpenEditorSupportInBrowser"/> から呼ばれる）。
    /// </summary>
    private async Task OpenEditorSupportSnapshotInBrowserAsync(string html, string? mapFolder, string title)
    {
        if (!_editorSupportNavigation.TryWritePage(html, out var pageUrl))
            return;

        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);
        var tab = CreateBrowserTab("about:blank", requestedTitle: title);
        await EnsureBrowserRealizedAsync(tab);
        if (tab.View.CoreWebView2 is not { } core)
            return;

        EditorSupportNavigationService.ConfigureVirtualHosts(core, mapFolder);
        core.Navigate(pageUrl);
        UpdateBrowserTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>ビジュアル提供者のビューをペインへ載せ、WebView2 を隠す（差し替え時は古いビューを外す）。</summary>
    private void ShowEditorSupportVisual(FrameworkElement view)
    {
        if (!ReferenceEquals(_editorSupportVisual, view))
        {
            if (_editorSupportVisual is not null)
                EditorSupportContentHost.Children.Remove(_editorSupportVisual);
            EditorSupportContentHost.Children.Add(view);
            _editorSupportVisual = view;
        }

        view.Visibility = Visibility.Visible;
        if (_editorSupportView is not null)
            _editorSupportView.Visibility = Visibility.Collapsed;
    }

    /// <summary>ビジュアル提供者のビューを隠し、WebView2 表示へ戻す。</summary>
    private void HideEditorSupportVisual()
    {
        if (_editorSupportVisual is not null)
            _editorSupportVisual.Visibility = Visibility.Collapsed;
        if (_editorSupportView is not null)
            _editorSupportView.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// ビジュアル提供者内での編集（CSV/TSV グリッド等）を、追従中のエディタタブの本文へ書き戻す。
    /// SetText で BufferChanged が発火しデバウンス更新が走るが、提供者側が内容比較で再パースを
    /// 抑止するためループしない。エディタタブは通常の編集と同じく未保存（modified）になる。
    /// </summary>
    private void EditorSupportVisual_ContentEdited(object? sender, EditorSupportContentEdited e)
    {
        var tab = _editorSupportSourceTab;
        if (tab is null
            || !string.Equals(tab.Control.FilePath, e.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        if (tab.Control.Text == e.Text)
            return;

        tab.Control.SetText(e.Text);
    }

    /// <summary>EditorSupport ペインを（無ければ Editor の右隣へ作って）表示する。明示プレビュー要求用。</summary>
    private void ShowEditorSupportPane()
    {
        if (IsPaneVisible(PaneKind.EditorSupport))
            return;

        EnsureEditorSupportLeafBesideEditor();
        SetPaneVisible(PaneKind.EditorSupport, true);
    }

    /// <summary>
    /// EditorSupport リーフがレイアウトツリーに無ければ Editor の右隣へ（隠した状態で）挿入する。
    /// 既定の <see cref="AddLeafAtBottom"/>（最下段の新しい行）よりプレビュー用途に適した位置になる。
    /// </summary>
    private void EnsureEditorSupportLeafBesideEditor()
    {
        // 跨ぎ最大化中は、解除時に戻す保存レイアウトにも同じ位置（Editor の右隣・隠した状態）で
        // 確保しておく（解除後の再表示位置が最下段の全幅行に落ちないように）。
        if (_isSpanMaximized && _spanSavedRoot is { } savedRoot
            && AllLeaves(savedRoot).All(l => l.Kind != PaneKind.EditorSupport)
            && AllLeaves(savedRoot).FirstOrDefault(l => l.Kind == PaneKind.Editor) is { } savedEditor)
        {
            _spanSavedRoot = InsertRelative(
                savedRoot, new PaneLeaf { Kind = PaneKind.EditorSupport, Hidden = true }, savedEditor, DropZone.Right);
        }

        if (FindLeaf(PaneKind.EditorSupport) is not null)
            return;
        if (FindLeaf(PaneKind.Editor) is not { } editorLeaf)
            return; // Editor がツリーに無い場合は SetPaneVisible の既定動作（最下段へ追加）に任せる

        CaptureLayoutSizes();
        _root = InsertRelative(_root, new PaneLeaf { Kind = PaneKind.EditorSupport, Hidden = true }, editorLeaf, DropZone.Right);
    }

    /// <summary>EditorSupport ペインの WebView2 を遅延生成し、CoreWebView2 まで実体化して返す（失敗時 null）。</summary>
    private async Task<WebView2CompositionControl?> EnsureEditorSupportViewAsync()
    {
        if (_editorSupportView is null)
        {
            _editorSupportView = new WebView2CompositionControl
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E),
                CreationProperties = CreateWebViewCreationProperties()
            };
            _editorSupportView.NavigationCompleted += OnEditorSupportNavigationCompleted;
            EditorSupportContentHost.Children.Add(_editorSupportView);
        }

        // 初期化は1回だけ。起動時に殺到する複数の描画要求が同じ初期化 Task を待つことで、
        // EnsureCoreWebView2Async の多重呼び出し（＝初回ナビゲーションのレース）を防ぐ。
        _editorSupportInitTask ??= InitializeEditorSupportCoreAsync(_editorSupportView);
        if (!await _editorSupportInitTask)
        {
            _editorSupportInitTask = null; // 失敗時は次回やり直せるようにする
            return null;
        }

        return _editorSupportView;
    }

    /// <summary>CoreWebView2 を実体化し、Web イベントと同梱アセットのホストマップを一度だけ設定する。</summary>
    private async Task<bool> InitializeEditorSupportCoreAsync(WebView2CompositionControl view)
    {
        try
        {
            await view.EnsureCoreWebView2Async();
        }
        catch
        {
            return false;
        }

        if (view.CoreWebView2 is null)
            return false;

        if (!_editorSupportWebEventsAttached)
        {
            view.CoreWebView2.WebMessageReceived += EditorSupport_WebMessageReceived;
            view.CoreWebView2.ContextMenuRequested += EditorSupport_ContextMenuRequested;

            EditorSupportNavigationService.ConfigureVirtualHosts(view.CoreWebView2, mapFolder: null);

            // 横チルトホイール（WM_MOUSEHWHEEL）は WebView2CompositionControl が web コンテンツへ
            // 転送しない（縦の WM_MOUSEWHEEL は転送される）ため、WPF 側の WndProc フックから "hscroll"
            // メッセージを送り、ページ側でポインタ直下の横あふれ要素をスクロールする。全ページ共通なので
            // ページ体裁（Markdown/JSON/コード/ログ）ごとの JS ではなく document-created で一度だけ注入する。
            try
            {
                await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HorizontalScrollScript);
            }
            catch
            {
                // 注入に失敗しても縦スクロール・本文表示は動く（横スクロールだけ効かない）。
            }

            _editorSupportWebEventsAttached = true;
        }

        return true;
    }

    /// <summary>
    /// EditorSupport の全 WebView ページへ注入する横スクロール補助スクリプト。ポインタ位置を追い、
    /// <c>hscroll</c> メッセージ（WPF の WM_MOUSEHWHEEL フック由来）で、ポインタ直下から辿った
    /// 最寄りの横スクロール可能要素（無ければドキュメント）を <c>dx</c> だけ横スクロールする。
    /// </summary>
    private const string HorizontalScrollScript = """
        (() => {
            let mx = 0, my = 0;
            addEventListener('mousemove', e => { mx = e.clientX; my = e.clientY; }, true);
            function scrollableX(el) {
                for (; el && el.nodeType === 1; el = el.parentElement) {
                    if (el.scrollWidth > el.clientWidth) {
                        const ox = getComputedStyle(el).overflowX;
                        if (ox === 'auto' || ox === 'scroll') return el;
                    }
                }
                return document.scrollingElement || document.documentElement;
            }
            window.chrome?.webview?.addEventListener('message', e => {
                const d = e.data;
                if (d && d.type === 'hscroll') {
                    const el = scrollableX(document.elementFromPoint(mx, my));
                    if (el) el.scrollLeft += d.dx;
                }
            });
        })();
        """;

    /// <summary>
    /// 横チルトホイールを EditorSupport の WebView コンテンツへ転送する（ポインタが WebView 上にある場合のみ）。
    /// WPF 側に横スクロール対象が無いとき（<see cref="HorizontalWheelScroll.Handle"/> が false）のフォールバック。
    /// </summary>
    internal bool TryHorizontalScrollEditorSupportWebView(int delta)
    {
        if (delta == 0
            || _editorSupportView is not { Visibility: Visibility.Visible, IsMouseOver: true } view
            || view.CoreWebView2 is not { } core)
            return false;

        try
        {
            core.PostWebMessageAsJson(FormattableString.Invariant($"{{\"type\":\"hscroll\",\"dx\":{delta}}}"));
            return true;
        }
        catch
        {
            // ナビゲーション中などで送れなくても落とさない（次のホイールで再送される）。
            return false;
        }
    }

    private void OnEditorSupportNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // URI（PDF 等）ナビゲートが失敗・中断したらガードを解除する。_editorSupportNavigatedUri は
        // Navigate 発行時に楽観的に確定するが、内蔵 PDF ビューアが file:// の遷移を取りこぼす等で
        // 失敗すると、ガードは「そのファイルを表示済み」と信じたまま残り、同じファイルを選び直しても
        // 再ナビゲートされず固まる（＝別 PDF へ切り替わらない）。失敗時に解除して再選択で復帰できるようにする。
        // HTML ページ描画中は _editorSupportNavigatedUri は null（RenderPendingEditorSupportContent で
        // クリア済み）なので、この解除は URI 分岐の失敗だけに効く。
        if (!e.IsSuccess && _editorSupportNavigatedUri is not null)
            _editorSupportNavigatedUri = null;

        // ページ読込が完了した＝ページ側スクリプトの setBody リスナが準備できた。以降この鍵の間は
        // 本文差し替え（フル再ナビゲートなし）を許す。読込中に新しいフル描画が始まっていれば、その
        // 鍵は次の完了で昇格するので、ここでは現在の loading 鍵をそのまま採用する。
        if (e.IsSuccess)
            _editorSupportReadyPageKey = _editorSupportLoadingPageKey;

        // 起動直後だけ、生成直後の WebView2 が初回ナビゲーションを取りこぼすことがある。最初の完了時に
        // 最新内容を一度だけ描き直して自己修復する（描き直しの完了は latch 済みなので再帰しない）。
        if (!_editorSupportFirstRenderHealed && _editorSupportView?.CoreWebView2 is { } core)
        {
            _editorSupportFirstRenderHealed = true;
            RenderPendingEditorSupportContent(core);
        }

        // コンテンツの描画が終わったら、エディタの現在位置までスクロールを合わせ直す。
        if (_editorSupportSourceTab is not null)
            PostEditorSupportScrollRatio(_editorSupportSourceTab.Control.VerticalScrollRatio);
    }

    private void DetachEditorSupportSource()
    {
        if (_editorSupportSourceTab is not null)
        {
            _editorSupportSourceTab.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
            _editorSupportSourceTab.Control.CaretMoved -= EditorSupportSource_CaretMoved;
        }
        StopCodeReadyRetry();
        _editorSupportSourceTab = null;
    }

    private void EditorSupportSource_ViewportScrolled(object? sender, EventArgs e)
    {
        if (_syncingEditorFromSupport || sender is not VimEditorControl editor)
            return;

        PostEditorSupportScrollRatio(editor.VerticalScrollRatio);
    }

    private void EditorSupport_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_editorSupportSourceTab is null)
            return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
                return;

            switch (typeElement.GetString())
            {
                case "markdownPreviewScroll":
                    if (root.TryGetProperty("ratio", out var ratioElement)
                        && ratioElement.TryGetDouble(out var ratio))
                    {
                        _syncingEditorFromSupport = true;
                        try { _editorSupportSourceTab.Control.ScrollToVerticalRatio(ratio); }
                        finally { _syncingEditorFromSupport = false; }
                    }
                    break;

                // JSON ツリー等から「↦ エディタで開く」：対応するソース行へカーソルを移してフォーカスを戻す。
                case "jumpToSource":
                    var line = root.TryGetProperty("line", out var lineElement)
                               && lineElement.TryGetInt32(out var l) ? l : 0;
                    FocusEditorSupportSource(line > 0 ? line : null);
                    break;

                // Markdown 本文中のリンククリック：http/https は内蔵ブラウザ、ファイルパスはエディタで開く。
                case "linkClicked":
                    if (root.TryGetProperty("href", out var hrefElement) && hrefElement.GetString() is { } href)
                        _ = HandleEditorSupportLinkClickedAsync(href);
                    break;

                // タスクリストのチェックボックスをプレビュー上でクリック：対応するソース行をエディタで書き換える。
                case "toggleTaskCheckbox":
                    if (root.TryGetProperty("line", out var taskLineElement) && taskLineElement.TryGetInt32(out var taskLine))
                        ToggleMarkdownTaskCheckbox(taskLine);
                    break;
            }
        }
        catch
        {
            // Ignore malformed messages from preview content.
        }
    }

    /// <summary>
    /// EditorSupport の追従元エディタへフォーカスを戻す（編集対象を見つけたら本文へ入る導線）。
    /// 行が指定されればその位置へカーソルを移す（JSON ツリーの「↦」）。指定なしは現在位置のまま
    /// フォーカスだけ移す（コンテキストメニュー／同期スクロール位置）。
    /// </summary>
    private void FocusEditorSupportSource(int? line, bool alignTop = false)
    {
        var tab = _editorSupportSourceTab;
        if (tab is null)
            return;

        // ソロ（舞台）モードなら Editor を舞台へ立ててから戻す。
        if (_stageActive && _stagePane != PaneKind.Editor)
            SetStagePane(PaneKind.Editor);

        SetActiveEditorTab(tab);
        if (line is int l)
        {
            // line は data-line（1 始まり）、NavigateTo は 0 始まりなので変換する
            // （Links.cs/CommandPalette.cs 等の他呼び出しと同じ規約。以前は 1 始まりのまま渡して
            // アウトライン・JSON/XML ツリーの ↦ ジャンプが 1 行下へずれていた）。
            tab.Control.NavigateTo(l - 1, 0);
            // コード構造アウトラインからのジャンプは、対象行を vim の zt 相当でビュー最上段へ寄せる。
            if (alignTop)
                tab.Control.ScrollCursorToTop();
        }
        tab.Control.Focus();
        _focusedRegion = FocusTarget.Of(PaneKind.Editor);
    }

    /// <summary>
    /// EditorSupport の WebView2 右クリックメニューへ「エディタへフォーカス」を足す。プレビューで
    /// 編集対象を見つけたら、そのまま追従元エディタへ戻れるようにする（全プレビュー共通の保険）。
    /// </summary>
    private void EditorSupport_ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (_editorSupportSourceTab is null || sender is not CoreWebView2 core)
            return;

        try
        {
            // プレビューは生成した 1 枚ページなので、既定メニューの「戻る／進む」（ブラウザのページ履歴）は
            // 意味を持たず、こちらのファイル履歴「前のファイルへ戻る」と同名で紛らわしい（戻るが 2 つ出る）。
            // 既定のページ内ナビ項目（back/forward、Name は非ローカライズの安定 ID）は取り除く。
            for (var i = e.MenuItems.Count - 1; i >= 0; i--)
            {
                if (e.MenuItems[i].Name is "back" or "forward")
                    e.MenuItems.RemoveAt(i);
            }

            var item = core.Environment.CreateContextMenuItem(
                "エディタへフォーカス", null, CoreWebView2ContextMenuItemKind.Command);
            item.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => FocusEditorSupportSource(null));
            e.MenuItems.Insert(0, item);

            // 前のファイルへ戻る（エディタのファイル履歴）。戻れる履歴が無ければ無効表示。
            var back = core.Environment.CreateContextMenuItem(
                "前のファイルへ戻る", null, CoreWebView2ContextMenuItemKind.Command);
            back.IsEnabled = _editorSupportHistory.CanGoBack;
            back.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = EditorSupportGoBackAsync());
            e.MenuItems.Insert(1, back);
        }
        catch
        {
            // メニュー項目を作れない環境でも、既定の右クリックメニューはそのまま出る。
        }
    }

    /// <summary>
    /// エディタの縦スクロール位置（比率）をプレビューへ送る。<c>ExecuteScriptAsync</c>（スクリプト文字列の
    /// 都度コンパイル＋IPC 往復待ち）ではなく <see cref="CoreWebView2.PostWebMessageAsJson"/> を使う
    /// ＝送りっぱなしで安く、連続スクロールでも待ち行列が詰まらない。間引き（1 フレーム 1 回の scrollTo）は
    /// ページ側の requestAnimationFrame が担う。エコー抑止もページ側 suppressScrollMessage が担う。
    /// </summary>
    private void PostEditorSupportScrollRatio(double ratio)
    {
        var core = _editorSupportView?.CoreWebView2;
        if (core is null)
            return;

        try
        {
            core.PostWebMessageAsJson(FormattableString.Invariant(
                $"{{\"type\":\"setScrollRatio\",\"ratio\":{Math.Clamp(ratio, 0.0, 1.0):R}}}"));
        }
        catch
        {
            // Best effort: the preview can be navigating while the editor scrolls.
        }
    }
}

