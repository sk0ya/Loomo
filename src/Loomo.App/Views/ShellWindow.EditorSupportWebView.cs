namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow に残る EditorSupport の View イベント配線。</summary>
public partial class ShellWindow
{
    private Task<WebView2CompositionControl?> EnsureEditorSupportViewAsync() => _editorSupportWebView.EnsureAsync();
    private void RenderPendingEditorSupportContent(CoreWebView2 core) => _editorSupportWebView.RenderPending(core);
    internal bool TryHorizontalScrollEditorSupportWebView(int delta) => _editorSupportWebView.TryHorizontalScroll(delta);
    private void PostEditorSupportScrollRatio(double ratio) => _editorSupportWebView.PostScrollRatio(ratio);

    // プレビューページの HTML を一時ファイルへ書き出し、page.loomo 経由のナビゲート URL を返す。 ?v= に毎回違う版番号を載せることで同一ファイルでも新 URL になり、WebView2 のキャッシュで 古いプレビューが居座らないようにする。書き出し失敗（権限・IO 等）時は false。 URL が EditorSupport の「ブラウザで開く」が書き出した一時プレビューページ（MarkdownRenderer.PageVirtualHost） を指しているか。ワークスペース保存時にこの手のタブを除外する判定に使う。 プレビュー HTML を一時ファイルへ書き出し、新規ブラウザタブでその仮想ホストを張ってから開く （OnOpenEditorSupportInBrowser から呼ばれる）。
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

    // ビジュアル提供者のビューをペインへ載せ、WebView2 を隠す（差し替え時は古いビューを外す）。
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
        if (_editorSupportWebView.View is not null)
            _editorSupportWebView.View.Visibility = Visibility.Collapsed;
    }

    // ビジュアル提供者のビューを隠し、WebView2 表示へ戻す。
    private void HideEditorSupportVisual()
    {
        if (_editorSupportVisual is not null)
            _editorSupportVisual.Visibility = Visibility.Collapsed;
        if (_editorSupportWebView.View is not null)
            _editorSupportWebView.View.Visibility = Visibility.Visible;
    }

    // ビジュアル提供者内での編集（CSV/TSV グリッド等）を、追従中のエディタタブの本文へ書き戻す。 SetText で BufferChanged が発火しデバウンス更新が走るが、提供者側が内容比較で再パースを 抑止するためループしない。エディタタブは通常の編集と同じく未保存（modified）になる。
    private void EditorSupportVisual_ContentEdited(object? sender, EditorSupportContentEdited e)
    {
        var tab = _editorSupport.Source;
        if (tab is null
            || !string.Equals(tab.Control.FilePath, e.FilePath, StringComparison.OrdinalIgnoreCase))
            return;

        if (tab.Control.Text == e.Text)
            return;

        tab.Control.SetText(e.Text);
    }

    // EditorSupport ペインを（無ければ Editor の右隣へ作って）表示する。明示プレビュー要求用。
    private void ShowEditorSupportPane()
    {
        if (IsPaneVisible(PaneKind.EditorSupport))
            return;

        EnsureEditorSupportLeafBesideEditor();
        SetPaneVisible(PaneKind.EditorSupport, true);
    }

    // EditorSupport リーフがレイアウトツリーに無ければ Editor の右隣へ（隠した状態で）挿入する。 既定の AddLeafAtBottom（最下段の新しい行）よりプレビュー用途に適した位置になる。
    private void EnsureEditorSupportLeafBesideEditor()
    {
        // 跨ぎ最大化中は、解除時に戻す保存レイアウトにも同じ位置（Editor の右隣・隠した状態）で 確保しておく（解除後の再表示位置が最下段の全幅行に落ちないように）。
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

    // EditorSupport ペインの WebView2 を遅延生成し、CoreWebView2 まで実体化して返す（失敗時 null）。


    private void DetachEditorSupportSource()
    {
        if (_editorSupport.DetachSource() is { } previous)
        {
            previous.Control.ViewportScrolled -= EditorSupportSource_ViewportScrolled;
            previous.Control.CaretMoved -= EditorSupportSource_CaretMoved;
        }
        StopCodeReadyRetry();
    }

    private void EditorSupportSource_ViewportScrolled(object? sender, EventArgs e)
    {
        if (_syncingEditorFromSupport || sender is not VimEditorControl editor)
            return;

        _editorSupportWebView.PostScrollRatio(editor.VerticalScrollRatio);
    }

    private void EditorSupport_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_editorSupport.Source is null)
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
                        try { _editorSupport.Source.Control.ScrollToVerticalRatio(ratio); }
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

    // EditorSupport の追従元エディタへフォーカスを戻す（編集対象を見つけたら本文へ入る導線）。 行が指定されればその位置へカーソルを移す（JSON ツリーの「↦」）。指定なしは現在位置のまま フォーカスだけ移す（コンテキストメニュー／同期スクロール位置）。
    private void FocusEditorSupportSource(int? line, bool alignTop = false)
    {
        var tab = _editorSupport.Source;
        if (tab is null)
            return;

        // ソロ（舞台）モードなら Editor を舞台へ立ててから戻す。
        if (_stageActive && _stagePane != PaneKind.Editor)
            SetStagePane(PaneKind.Editor);

        SetActiveEditorTab(tab);
        if (line is int l)
        {
            // line は data-line（1 始まり）、NavigateTo は 0 始まりなので変換する （Links.cs/CommandPalette.cs 等の他呼び出しと同じ規約。以前は 1 始まりのまま渡して アウトライン・JSON/XML ツリーの ↦ ジャンプが 1 行下へずれていた）。
            tab.Control.NavigateTo(l - 1, 0);
            // コード構造アウトラインからのジャンプは、対象行を vim の zt 相当でビュー最上段へ寄せる。
            if (alignTop)
                tab.Control.ScrollCursorToTop();
        }
        tab.Control.Focus();
        _focusedRegion = FocusTarget.Of(PaneKind.Editor);
    }

    // EditorSupport の WebView2 右クリックメニューへ「エディタへフォーカス」を足す。プレビューで 編集対象を見つけたら、そのまま追従元エディタへ戻れるようにする（全プレビュー共通の保険）。
    private void EditorSupport_ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (_editorSupport.Source is null || sender is not CoreWebView2 core)
            return;

        try
        {
            // プレビューは生成した 1 枚ページなので、既定メニューの「戻る／進む」（ブラウザのページ履歴）は 意味を持たず、こちらのファイル履歴「前のファイルへ戻る」と同名で紛らわしい（戻るが 2 つ出る）。 既定のページ内ナビ項目（back/forward、Name は非ローカライズの安定 ID）は取り除く。
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
            back.IsEnabled = _editorSupport.History.CanGoBack;
            back.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(() => _ = EditorSupportGoBackAsync());
            e.MenuItems.Insert(1, back);
        }
        catch
        {
            // メニュー項目を作れない環境でも、既定の右クリックメニューはそのまま出る。
        }
    }

    // エディタの縦スクロール位置（比率）をプレビューへ送る。ExecuteScriptAsync（スクリプト文字列の 都度コンパイル＋IPC 往復待ち）ではなく CoreWebView2.PostWebMessageAsJson を使う ＝送りっぱなしで安く、連続スクロールでも待ち行列が詰まらない。間引き（1 フレーム 1 回の scrollTo）は ページ側の requestAnimationFrame が担う。エコー抑止もページ側 suppressScrollMessage が担う。
}
