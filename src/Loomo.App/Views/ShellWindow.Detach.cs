using System.Collections.ObjectModel;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン項目の別ウィンドウ切り離し。Editor は同一ファイルの複製＋双方向テキスト同期、 Terminal/Browser は同期なしの新規スピンオフ。ウィンドウ管理・タブ結合は <see cref="DetachedWindowManager"/>。 状態はワークスペースのスナップショットへ保存し、切替・再起動時に復元する。</summary>
public partial class ShellWindow {
    private DetachedWindowManager? _detached;
    private DetachedWindowManager Detached => _detached ??= new DetachedWindowManager(this, () => SaveActiveWorkspaceSnapshot());
    private DetachedItemSnapshot? CaptureDetachedItem(DetachedItem item) {
        var snapshot = new DetachedItemSnapshot { Kind = item.Kind.ToString() };
        switch (item.Content) {
            case VimEditorControl editor:
                snapshot.FilePath = editor.FilePath;
                snapshot.Text = editor.IsModified || string.IsNullOrWhiteSpace(editor.FilePath) ? editor.Text : null;
                snapshot.IsModified = editor.IsModified;
                break;
            case TerminalTabView terminal:
                snapshot.WorkingDirectory = terminal.WorkingDirectory;
                break;
            case WebView2CompositionControl browser:
                snapshot.Url = browser.Source?.ToString();
                break;
            // 切り離したブラウザは作り直せるよう器（Grid）越しに載っている（CreateBrowserSpinoffItem）。
            // 器のまま素通りさせると復元対象から外れ、切り替え・再起動でその窓だけ消える。
            case Panel host when host.Children.OfType<WebView2CompositionControl>().FirstOrDefault() is { } hosted:
                snapshot.Url = hosted.Source?.ToString();
                break;
            case DetachedEditorSupportView preview:
                snapshot.FilePath = preview.SourceFilePath;
                break;
            default:
                return null;
        }
        return snapshot;
    }
    private DetachedItem? RestoreDetachedItem(DetachedItemSnapshot snapshot) {
        if (!Enum.TryParse<DetachKind>(snapshot.Kind, out var kind)) return null;
        if (kind == DetachKind.EditorMirror && !string.IsNullOrWhiteSpace(snapshot.FilePath)) {
            var source = _editorTabs.FirstOrDefault(t => string.Equals( t.PeekFilePath, snapshot.FilePath, StringComparison.OrdinalIgnoreCase));
            if (source is not null) return TryCreateEditorMirrorItem(source.Id);
        }
        if (kind is DetachKind.EditorMirror or DetachKind.EditorMove) {
            var tab = CreateEditorTab();
            var editor = tab.Control;
            if (!string.IsNullOrWhiteSpace(snapshot.FilePath) && File.Exists(snapshot.FilePath))
                LoadEditorFile(editor, snapshot.FilePath);
            if (snapshot.Text is not null) editor.SetText(snapshot.Text);
            var title = string.IsNullOrWhiteSpace(snapshot.FilePath) ? "Untitled" : Path.GetFileName(snapshot.FilePath);
            return new DetachedItem(DetachKind.EditorMove, title, editor, _tabIcons.GetFileIcon(snapshot.FilePath), editor.Dispose) {
                Return = new DetachReturn(TabEntryKind.Editor, () => AdoptEditorTab(tab))
            };
        }
        if (kind == DetachKind.EditorSupportMirror && !string.IsNullOrWhiteSpace(snapshot.FilePath)) {
            var source = _editorTabs.FirstOrDefault(t => string.Equals( t.PeekFilePath, snapshot.FilePath, StringComparison.OrdinalIgnoreCase));
            if (source is null) return null;
            var view = new DetachedEditorSupportView(_editorSupportResolver, _editorSupport.Pipeline, _editorSupport.WebView.ViewFactory, _settings, _workspace, source.Control);
            var item = new DetachedItem(kind, $"Preview: {Path.GetFileName(snapshot.FilePath)}", view, dispose: view.Dispose);
            view.TitleChanged += (_, title) => item.Title = title;
            AttachEditorSupportMirrorLinks(view);
            return item;
        }
        if (kind is DetachKind.TerminalSpinoff or DetachKind.TerminalMove)
            return CreateTerminalSpinoffItem(snapshot.WorkingDirectory);
        if (kind == DetachKind.BrowserSpinoff)
            return CreateBrowserSpinoffItem(snapshot.Url);
        return null;
    }
    private void OnSidebarTabDetachRequested(object? sender, TabEntryViewModel tab) {
        DetachedItem? item = tab.Kind switch {
            TabEntryKind.Editor => TryCreateEditorMirrorItem(tab.Id), TabEntryKind.Terminal => CreateTerminalSpinoffItem(_terminalTabs.FirstOrDefault(t => t.Id == tab.Id)), TabEntryKind.Browser => CreateBrowserSpinoffItem(_browserTabs.FirstOrDefault(t => t.Id == tab.Id)), _ => null
        };
        if (item is not null)
            Detached.Detach(item);
    }
    private void OnDetachEditorPane(object sender, RoutedEventArgs e) {
        var id = _editorViews?.FocusedTabId ?? _activeEditorTab?.Id;
        if (id is { } tabId && TryCreateEditorMirrorItem(tabId) is { } item)
            Detached.Detach(item);
    }
    private void OnDetachTerminalPane(object sender, RoutedEventArgs e) {
        var src = _terminalViews?.FocusedTabId is { } id
            ? _terminalTabs.FirstOrDefault(t => t.Id == id)
            : _activeTerminalTab;
        Detached.Detach(CreateTerminalSpinoffItem(src));
    }
    private void OnDetachBrowserPane(object sender, RoutedEventArgs e)
        => Detached.Detach(CreateBrowserSpinoffItem(_activeBrowserTab));
    private void OnDetachEditorSupport(object sender, RoutedEventArgs e) {
        var source = (_editorSupport.Source ?? _activeEditorTab)?.Control;
        if (source is null)
            return;
        var view = new DetachedEditorSupportView(_editorSupportResolver, _editorSupport.Pipeline, _editorSupport.WebView.ViewFactory, _settings, _workspace, source);
        var title = string.IsNullOrWhiteSpace(source.FilePath)
            ? "Preview"
            : $"Preview: {Path.GetFileName(source.FilePath!)}";
        var item = new DetachedItem( DetachKind.EditorSupportMirror, title, view, dispose: view.Dispose);
        view.TitleChanged += (_, t) => item.Title = t;
        AttachEditorSupportMirrorLinks(view);
        Detached.Detach(item);
    }
    private void AttachEditorSupportMirrorLinks(DetachedEditorSupportView view) {
        view.LinkClicked += async (_, href) => {
            await HandleEditorSupportLinkClickedAsync(href, view.SourceFilePath);
            Activate();
        };
        // 右クリックした本文中リンクを、さらに別ウィンドウで開く（メイン側の EditorSupport と同じ動線）。
        view.LinkWindowMenu = href => {
            var target = LinkOpenTargetResolver.Resolve(_workspace, href, view.SourceFilePath);
            return DescribeOpenLinkInWindow(target) is { } header
                ? (header, (Action)(() => OpenLinkTargetInDetachedWindow(target)))
                : null;
        };
        // 切り離した時点の検索ハイライトを引き継ぐ（以降は ApplyEditorSupportSearchHighlight が配る）。
        var search = _vm.SearchPanel;
        view.SetSearchHighlight(
            search.SupportHighlightTerm, search.HighlightCaseSensitive, search.HighlightUseRegex);
    }
    /// <summary>リンク先のファイルを別ウィンドウのエディタで開く。メインのタブは増やさない独立コントロールで、
    /// 追従元も持たないので <see cref="DetachKind.EditorMove"/>（複製なし）として扱う＝復元も同じファイルを開き直す。</summary>
    private void OpenPathInDetachedWindow(string fullPath, int line = 0, int column = 0) {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            return;
        var tab = CreateEditorTab();
        var control = tab.Control;
        LoadEditorFile(control, fullPath);
        if (line > 0) {
            try { control.NavigateTo(line - 1, column > 0 ? column - 1 : 0); }
            catch { /* 行番号が本文より後ろなら内部でクランプ */ }
        }
        Detached.Detach(new DetachedItem( DetachKind.EditorMove, Path.GetFileName(fullPath), control, _tabIcons.GetFileIcon(fullPath), dispose: control.Dispose) {
            Return = new DetachReturn(TabEntryKind.Editor, () => AdoptEditorTab(tab))
        });
    }
    /// <summary>リンク先の URL を別ウィンドウのブラウザで開く（同期なしのスピンオフ）。</summary>
    private void OpenUrlInDetachedWindow(string url) {
        if (string.IsNullOrWhiteSpace(url))
            return;
        Detached.Detach(CreateBrowserSpinoffItem(url));
    }
    /// <summary>解決済みリンク先を種別に応じた別ウィンドウで開く（エディタ／EditorSupport の右クリック共通）。</summary>
    private void OpenLinkTargetInDetachedWindow(LinkOpenTarget target) {
        switch (target.Kind) {
            case LinkOpenTargetKind.Url:
                OpenUrlInDetachedWindow(target.Value);
                break;
            case LinkOpenTargetKind.File:
                OpenPathInDetachedWindow(target.Value, target.Line, target.Column);
                break;
        }
    }
    private DetachedItem? TryCreateEditorMirrorItem(Guid sourceTabId) {
        var src = _editorTabs.FirstOrDefault(t => t.Id == sourceTabId);
        if (src is null)
            return null;
        var srcCtl = src.Control;                 // 未実体化なら実体化
        var mirrorTab = CreateEditorTab();         // 独立コントロール（_editorTabs には加えない＝非永続）
        var mirror = mirrorTab.Control;
        if (!string.IsNullOrWhiteSpace(srcCtl.FilePath) && File.Exists(srcCtl.FilePath) && !srcCtl.IsModified)
            LoadEditorFile(mirror, srcCtl.FilePath);
        else
            mirror.SetText(srcCtl.Text);
        var syncing = false;
        void Sync(VimEditorControl from, VimEditorControl to) {
            if (syncing || string.Equals(to.Text, from.Text, StringComparison.Ordinal))
                return;
            syncing = true;
            try {
                var caret = to.Caret;
                to.SetText(from.Text);
                try { to.NavigateTo(caret.Line, caret.Column); } catch { /* 縮んだ本文で範囲外なら内部でクランプ */ }
            } finally { syncing = false; }
        }
        EventHandler srcHandler = (_, _) => Sync(srcCtl, mirror);
        EventHandler mirHandler = (_, _) => Sync(mirror, srcCtl);
        srcCtl.BufferChanged += srcHandler;
        mirror.BufferChanged += mirHandler;
        void Unsync() {
            srcCtl.BufferChanged -= srcHandler;
            mirror.BufferChanged -= mirHandler;
        }
        var title = string.IsNullOrWhiteSpace(srcCtl.FilePath) ? "Untitled" : Path.GetFileName(srcCtl.FilePath!);
        return new DetachedItem( DetachKind.EditorMirror, title, mirror, _tabIcons.GetFileIcon(srcCtl.FilePath), dispose: () => {
                Unsync();
                mirror.Dispose();
            }) {
            // 帯へ戻すときは追従を切ってから独立したタブとして迎える——同期したまま並ぶと、
            // 同じファイルの2枚が一緒に動いて別々に編集できない。
            Return = new DetachReturn(TabEntryKind.Editor, () => { Unsync(); AdoptEditorTab(mirrorTab); })
        };
    }
    private DetachedItem CreateTerminalSpinoffItem(TerminalTab? sourceTab)
        => CreateTerminalSpinoffItem(sourceTab?.View.WorkingDirectory);
    private DetachedItem CreateTerminalSpinoffItem(string? sourceDirectory) {
        var cwd = sourceDirectory;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            cwd = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
        var view = new TerminalTabView("pwsh.exe", cwd) { AutoFocusOnStart = false };
        _appearance.ApplyTerminalAppearance(view);
        // メインの帯へ戻すときはここで張った見出し追従を外し、メインのタブとしての配線を張り直す
        // （タブの実体＝生きたセッションはそのまま運ぶ）。
        return CreateDetachedTerminalItem(DetachKind.TerminalSpinoff, view, () => HookTerminalTab(new TerminalTab(Guid.NewGuid(), view)));
    }
    /// <summary>切り離しウィンドウのターミナルタブ（スピンオフも移動も同じ形）。見出しはセッションに追従し、
    /// メインの帯へ落とせば <paramref name="mainTab"/> が返すタブとして戻る。</summary>
    private DetachedItem CreateDetachedTerminalItem(
        DetachKind kind, TerminalTabView view, Func<TerminalTab> mainTab) {
        DetachedItem? item = null;
        void OnTitle(object? _, string title)
            => item!.Title = string.IsNullOrWhiteSpace(title) ? "Terminal" : title;
        item = new DetachedItem(
            kind,
            string.IsNullOrWhiteSpace(view.HeaderTitle) ? "Terminal" : view.HeaderTitle,
            view, _tabIcons.GetTerminalIcon(),
            dispose: () => { view.HeaderTitleChanged -= OnTitle; _ = view.CloseAsync(); }) {
            Return = new DetachReturn(TabEntryKind.Terminal, () => {
                view.HeaderTitleChanged -= OnTitle;
                AdoptTerminalTab(mainTab());
            })
        };
        view.HeaderTitleChanged += OnTitle;
        return item;
    }
    private DetachedItem CreateBrowserSpinoffItem(BrowserTab? sourceTab)
        => CreateBrowserSpinoffItem(BrowserUrlOf(sourceTab));
    /// <summary>切り離したブラウザ。<see cref="DetachedItem.Content"/> は差し替えられないので、
    /// <b>器（Grid）を挟んで</b>中の WebView2 だけを作り直せるようにする——ブラウザプロセスが落ちたら
    /// コントロールごと作り直すため（§21.5.3。共有プロファイルなので他インスタンスの巻き添えでも落ちる）。</summary>
    private DetachedItem CreateBrowserSpinoffItem(string? sourceUrl) {
        var url = sourceUrl ?? DefaultBrowserUrl;
        var host = new Grid();
        var view = CreateBrowserView();
        view.Visibility = Visibility.Visible;
        host.Children.Add(view);
        var item = new DetachedItem( DetachKind.BrowserSpinoff, "Browser", host, _tabIcons.GetBrowserDefaultIcon(), dispose: () => DisposeSpinoffBrowser(host)) {
            // 戻すときは<b>作り直す</b>——WebView2（コンポジション版）は窓をまたいで載せ替えると
            // コンポジタが元の窓に残って空表示になる（引き出すときも同じ理由で新規生成している）。
            Return = new DetachReturn(TabEntryKind.Browser, () => {
                var current = host.Children.OfType<WebView2CompositionControl>().FirstOrDefault()?.Source?.ToString();
                DisposeSpinoffBrowser(host);
                _ = CreateBrowserTabAsync(current ?? url);
                FocusPane(PaneKind.Browser);
            })
        };
        _ = RealizeSpinoffBrowserAsync(host, view, url, item);
        return item;
    }
    /// <summary>
    /// 差分ひとつを、切り離しウィンドウのタブ1枚として作る（Git のコミット詳細のダブルクリック＝
    /// 送るたびに新しい窓と、Diff ペインが隠れているときの差分の行き先＝同じ窓へタブを足す、の共通の実体）。
    ///
    /// <para>VM は <see cref="DiffSessionFactory"/> で<b>もう1つ立てる</b>——DIFF ペインの VM は部屋が
    /// ひとつ持っている状態なので、そこへ流し込むとペインで見ていた差分が奪われる。立てた VM は共有
    /// Singleton（GitService・比較基準）を購読するので、窓を閉じるときに <c>Dispose</c> する。</para>
    ///
    /// <para>ビューは XAML から生えないぶん、ペインが ShellWindow のコンストラクタで受け取っているのと
    /// 同じ物（レンダリング表示の WebView2 ファクトリ、リンク・エディタ行への中継）をここで渡す。
    /// 一時ページ名だけはペインと分ける（既定名のままだと互いの本文を上書きし合う）。</para>
    /// </summary>
    private DetachedItem CreateDiffSpinoffItem(DiffOpenTarget target) {
        var vm = _diffSessions.Create();
        var view = new DiffSessionView { DataContext = vm };
        // この窓にはペインヘッダーが無い＝ヘッダーへ集約した操作（次/前の差分・エディタで開く・
        // 左右/統合・Markdown 描画・Git 一覧で表示・比較の入替/再比較/閉じる）が丸ごと欠ける。
        // ビュー自前のバーで同じ物を出す。
        view.ShowStandaloneToolbar();
        view.ConfigureMarkdownRender(
            _editorSupport.WebView.ViewFactory, EditorSupportPreviewFolder, Guid.NewGuid().ToString("N"));
        view.MarkdownLinkClicked += (_, e) => _ = HandleEditorSupportLinkClickedAsync(e.Href, e.SourcePath);
        // 差分の行から実ファイルを開く／コミットを Git ペインで選ぶ——どちらもメイン側の持ち場なので、
        // ペインの VM と同じ経路へ中継してメインウィンドウを前に出す。
        vm.EditorLineOpenRequested += async (_, t) => {
            await OpenPathInEditorAsync(Path.GetFullPath(t.Path), t.Line, column: 0);
            Activate();
            FocusPane(PaneKind.Editor);
        };
        vm.CommitOpenInGitRequested += async (_, hash) => {
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Git);
            await _vm.GitSession.SelectCommitAsync(hash);
            Activate();
            FocusPane(PaneKind.Git);
        };
        var item = new DetachedItem(
            DetachKind.DiffSpinoff, target.WindowTitle, view, _tabIcons.GetFileIcon(target.IconPath),
            vm.Dispose);
        // 「次の差分」はファイルの端を越えると隣のファイルへ移る（＝窓の中身が別ファイルになる）ので、
        // タブのタイトルとアイコンも今見ているファイルへ追従させる——開いたときの名前のままだと、
        // どのファイルを読んでいるのか窓の側から分からなくなる。
        vm.PropertyChanged += (_, e) => {
            if (e.PropertyName != nameof(DiffSessionViewModel.SelectedFile)) return;
            if (vm.SelectedFile is not { } file) return;
            item.Title = file.Comparison is { } compare
                ? new DiffOpenTarget.Comparison(compare).WindowTitle    // 比較は左右の名前で名乗る
                : target.TitleFor(file.FullPath);
            item.Icon = _tabIcons.GetFileIcon(file.FullPath);
        };
        _ = ShowDiffInWindowAsync(vm, target);
        return item;
    }
    private static void DisposeSpinoffBrowser(Panel host) {
        foreach (var view in host.Children.OfType<WebView2CompositionControl>().ToList())
            try { view.Dispose(); } catch { }
    }
    private async Task RealizeSpinoffBrowserAsync(
        Panel host, WebView2CompositionControl view, string url, DetachedItem item) {
        try { await view.EnsureCoreWebView2Async(); }
        catch { WebViewEnvironment.ReportUnavailable("ブラウザ"); return; }
        WebViewEnvironment.NoteCreated();
        if (view.TryCore() is not { } core)
            return;   // 生成直後に落ちた（作り直しは ProcessFailed 経由）
        ConfigureBrowserCoreBasics(core);
        var rendererReloads = 0;
        view.NavigationCompleted += (_, e) => { if (e.IsSuccess) rendererReloads = 0; };   // 描けたら仕切り直す
        core.ProcessFailed += (_, e) => {
            if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited) {
                // 描画プロセスだけの死は読み直しで戻る。ただし回数を区切る（確実に描画を殺すページだと
                // 読み直すたびに落ちて堂々巡りになる。本体ペインと同じ歯止め）。
                if (rendererReloads++ < MaxRendererReloads)
                    try { view.TryCore()?.Reload(); } catch { }
                return;
            }
            // 落ちる前の行き先を控えてから、器の中身を作り直す（イベント配布中に壊さない）。
            var last = view.Source?.ToString();
            Dispatcher.BeginInvoke(new Action(() => RebuildSpinoffBrowser(host, item, last ?? url)));
        };
        // 切り離した窓の target="_blank" は、素の WebView2 の既定（ツールバーの無い素っ気ない窓）ではなく
        // もう1枚の切り離しウィンドウで受ける（本体ペインの新しいタブと同じ考え方）。
        core.NewWindowRequested += (_, e) => {
            e.Handled = true;
            var uri = e.Uri;
            Dispatcher.BeginInvoke(new Action(() => OpenUrlInDetachedWindow(uri)));
        };
        core.DocumentTitleChanged += (_, _) => {
            var title = view.TryCore()?.DocumentTitle;
            item.Title = string.IsNullOrWhiteSpace(title) ? "Browser" : title!;
        };
        try { view.Source = new Uri(WorkspaceSessionCoordinator.NormalizeBrowserAddress(url, DefaultBrowserUrl)); }
        catch { /* 不正 URL は無視（空ページのまま） */ }
    }
    private void RebuildSpinoffBrowser(Panel host, DetachedItem item, string url) {
        // 落ちた知らせは窓から外れた後にも届く（Dispatcher 経由なので1拍遅れる）。手放した器へ作り直すと、
        // 誰にも見えない WebView2 が残る——メインへ<b>戻した</b>ときは新しいタブとして作り直し済みなので、
        // ブラウザが2つに増えてしまう。どの窓にも居ない項目なら何もしない。
        if (!Detached.AllItems.Contains(item))
            return;
        DisposeSpinoffBrowser(host);
        host.Children.Clear();
        var view = CreateBrowserView();
        view.Visibility = Visibility.Visible;
        host.Children.Add(view);
        _ = RealizeSpinoffBrowserAsync(host, view, url, item);
    }
    private Point _paneTabDragStart;
    private Guid _paneTabDragId;
    private bool _paneTabDragArmed;
    private void OnPaneTabPreviewMouseDown(object sender, MouseButtonEventArgs e) {
        _paneTabDragArmed = false;
        if (ResolvePaneTabId(e.OriginalSource) is { } id) {
            _paneTabDragStart = e.GetPosition(this);
            _paneTabDragId = id;
            _paneTabDragArmed = true;
        }
    }
    private void OnPaneTabPreviewMouseMove(object sender, MouseEventArgs e) {
        if (_paneTabReordering) {
            HandlePaneTabReorderMove(e);
            return;
        }
        if (!_paneTabDragArmed || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(this);
        var dx = Math.Abs(pos.X - _paneTabDragStart.X);
        var dy = Math.Abs(pos.Y - _paneTabDragStart.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance && dy < SystemParameters.MinimumVerticalDragDistance)
            return;
        _paneTabDragArmed = false;
        // ほぼ水平のドラッグだけ並べ替えとして扱う。縦方向にも動いた場合は既存の切り離し（別ウィンドウ化）を優先する
        // ―― 切り離しは既存のドキュメント化済み機能なので、その発火条件を極力変えないため。
        if (dy <= PaneTabReorderVerticalTolerance && dx >= SystemParameters.MinimumHorizontalDragDistance
            && sender is ItemsControl host) {
            StartPaneTabReorder(_paneTabDragId, host);
            return;
        }
        StartPaneTabTearOff(_paneTabDragId, sender as UIElement);
    }
    private const double PaneTabReorderVerticalTolerance = 6.0;
    private bool _paneTabReordering;
    private ItemsControl? _paneTabReorderHost;
    private Guid _paneTabReorderId;
    private void StartPaneTabReorder(Guid id, ItemsControl host) {
        _paneTabReordering = true;
        _paneTabReorderHost = host;
        _paneTabReorderId = id;
        host.PreviewMouseLeftButtonUp += OnPaneTabReorderMouseUp;
        Mouse.Capture(host);
    }
    private void HandlePaneTabReorderMove(MouseEventArgs e) {
        if (e.LeftButton != MouseButtonState.Pressed) {
            EndPaneTabReorder();
            return;
        }
        if (_paneTabReorderHost is not { } host)
            return;
        var pos = e.GetPosition(host);
        if (pos.X < 0 || pos.Y < 0 || pos.X > host.ActualWidth || pos.Y > host.ActualHeight)
            return;
        if (VisualTreeHelper.HitTest(host, pos)?.VisualHit is not { } hit)
            return;
        if (ResolvePaneTabId(hit) is not { } targetId || targetId == _paneTabReorderId)
            return;
        MovePaneTab(_paneTabReorderId, targetId);
    }
    private void OnPaneTabReorderMouseUp(object sender, MouseButtonEventArgs e) => EndPaneTabReorder();
    private void EndPaneTabReorder() {
        if (_paneTabReorderHost is { } host)
            host.PreviewMouseLeftButtonUp -= OnPaneTabReorderMouseUp;
        if (Mouse.Captured is not null)
            Mouse.Capture(null);
        var wasReordering = _paneTabReordering;
        _paneTabReordering = false;
        _paneTabReorderHost = null;
        if (wasReordering)
            SaveActiveWorkspaceSnapshot();
    }
    /// <summary>タブ帯上のドラッグ並べ替え。コードビハインドの実体リスト（<see cref="_editorTabs"/> 等、
    /// タブ切替・ワークスペース復元が位置参照する）と ViewModel 側の <see cref="TabsViewModel"/> 表示用
    /// コレクションの両方を同じ並びに保つ。</summary>
    private void MovePaneTab(Guid draggedId, Guid targetId) {
        if (TryReorderList(_editorTabs, t => t.Id, draggedId, targetId, out var index)) {
            MoveObservableTab(_vm.Tabs.EditorTabs, draggedId, index);
            return;
        }
        if (TryReorderList(_terminalTabs, t => t.Id, draggedId, targetId, out index)) {
            MoveObservableTab(_vm.Tabs.TerminalTabs, draggedId, index);
            return;
        }
        if (TryReorderList(_browserTabs, t => t.Id, draggedId, targetId, out index))
            MoveObservableTab(_vm.Tabs.BrowserTabs, draggedId, index);
    }
    private static bool TryReorderList<T>(List<T> list, Func<T, Guid> idOf, Guid draggedId, Guid targetId, out int newIndex) {
        newIndex = -1;
        var oldIndex = list.FindIndex(t => idOf(t) == draggedId);
        if (oldIndex < 0)
            return false;
        var targetIndex = list.FindIndex(t => idOf(t) == targetId);
        if (targetIndex < 0 || targetIndex == oldIndex)
            return false;
        var item = list[oldIndex];
        list.RemoveAt(oldIndex);
        list.Insert(targetIndex, item);
        newIndex = targetIndex;
        return true;
    }
    private static void MoveObservableTab(ObservableCollection<TabEntryViewModel> tabs, Guid id, int newIndex) {
        var oldIndex = -1;
        for (var i = 0; i < tabs.Count; i++) {
            if (tabs[i].Id == id) { oldIndex = i; break; }
        }
        if (oldIndex >= 0 && oldIndex != newIndex)
            tabs.Move(oldIndex, newIndex);
    }
    /// <summary>タブ帯の「▾」：あふれて見えなくなったタブも含む全件を一覧表示し、クリックで直接アクティブ化する。</summary>
    private void OnTabOverflowClick(object sender, RoutedEventArgs e) {
        if (sender is not FrameworkElement { Tag: string kind } button)
            return;
        var tabs = kind switch {
            "Terminal" => _vm.Tabs.TerminalTabs,
            "Editor" => _vm.Tabs.EditorTabs,
            "Browser" => _vm.Tabs.BrowserTabs,
            _ => null,
        };
        if (tabs is null)
            return;
        BuildTabOverflowPopup(kind, tabs);
        TabOverflowPopup.PlacementTarget = button;
        TabOverflowPopup.IsOpen = true;
    }
    private void BuildTabOverflowPopup(string kind, ObservableCollection<TabEntryViewModel> tabs) {
        TabOverflowPopupList.Children.Clear();
        if (tabs.Count == 0) {
            TabOverflowPopupList.Children.Add(new TextBlock {
                Text = "タブがありません", FontSize = UiFontManager.Scaled(12), Margin = new Thickness(10, 6, 10, 6),
                Foreground = (Brush)FindResource("FgDim"),
            });
            return;
        }
        foreach (var tab in tabs) {
            var captured = tab;
            var content = new TextBlock {
                Text = tab.Title, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = tab.IsActive ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var row = new Button {
                Style = (Style)FindResource("BranchMenuItem"), FontSize = UiFontManager.Scaled(12),
                ToolTip = tab.FilePath ?? tab.Title, Content = content, HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            row.Click += (_, _) => {
                TabOverflowPopup.IsOpen = false;
                switch (kind) {
                    case "Terminal": ActivateTerminalTab(captured.Id); break;
                    case "Editor": ActivateEditorTab(captured.Id); break;
                    case "Browser": ActivateBrowserTab(captured.Id); break;
                }
            };
            TabOverflowPopupList.Children.Add(row);
        }
    }
    /// <summary>ペインのタブを帯の外へ引き出すドラッグ。切り離しウィンドウ側と同じ演出——運んでいるタブを
    /// カーソルに付け（<see cref="TabDragGhost"/>）、元のタブは薄く残す（設計書 §21.4 の「タブが動く」の続き）。</summary>
    private void StartPaneTabTearOff(Guid id, UIElement? source) {
        if (source is null || BuildTearOffFactory(id) is not { } factory)
            return;
        if (Mouse.Captured is not null)
            Mouse.Capture(null);
        var entry = FindPaneTabEntry(id);
        var container = entry is not null && source is ItemsControl host
            ? host.ItemContainerGenerator.ContainerFromItem(entry) as FrameworkElement
            : null;
        if (container is not null)
            container.Opacity = TabDragGhost.TornSourceOpacity;
        using var ghost = TabDragGhost.Show(this, entry?.Title ?? "タブ", entry?.Icon);
        void OnGiveFeedback(object _, GiveFeedbackEventArgs e) => ghost.Follow(e.Effects);
        source.GiveFeedback += OnGiveFeedback;
        Detached.BeginExternalDrag(factory, ghost);
        QueryContinueDragEventHandler onQcd = (_, e) => { if (e.EscapePressed) Detached.CancelDrag(); };
        source.QueryContinueDrag += onQcd;
        try {
            var data = new DataObject(DetachedPaneWindow.DetachDragFormat, "external");
            var result = DragDrop.DoDragDrop(source, data, DragDropEffects.Move);
            Detached.EndDrag(result);
        } finally {
            source.QueryContinueDrag -= onQcd;
            source.GiveFeedback -= OnGiveFeedback;
            if (container is not null)
                container.Opacity = 1;   // 引き出せていれば元タブごと消えている（残ったときのために戻す）
            Detached.ClearDrag();
        }
    }
    /// <summary>タブ帯の表示用エントリ（ゴーストに出す名前とアイコンの出どころ）。</summary>
    private TabEntryViewModel? FindPaneTabEntry(Guid id)
        => _vm.Tabs.EditorTabs.FirstOrDefault(t => t.Id == id)
           ?? _vm.Tabs.TerminalTabs.FirstOrDefault(t => t.Id == id)
           ?? _vm.Tabs.BrowserTabs.FirstOrDefault(t => t.Id == id);
    private Func<DetachedItem>? BuildTearOffFactory(Guid id) {
        if (_editorTabs.Any(t => t.Id == id))
            return () => {
                // タブの実体（EditorTab）ごと運ぶ。戻すときも同じ実体を帯へ戻すので、タブ ID も
                // コントロールに張った配線（見出し更新・軌跡・EditorSupport 追従）も切り離す前のまま続く。
                var tab = RemoveEditorTabForMove(id)!;
                var control = tab.Control;
                var title = string.IsNullOrWhiteSpace(control.FilePath) ? "Untitled" : Path.GetFileName(control.FilePath!);
                return new DetachedItem( DetachKind.EditorMove, title, control, _tabIcons.GetFileIcon(control.FilePath), dispose: control.Dispose) {
                    Return = new DetachReturn(TabEntryKind.Editor, () => AdoptEditorTab(tab))
                };
            };
        if (_terminalTabs.Any(t => t.Id == id))
            return () => {
                var tab = RemoveTerminalTabForMove(id)!;
                return CreateDetachedTerminalItem(DetachKind.TerminalMove, tab.View, () => tab);
            };
        if (_browserTabs.Any(t => t.Id == id))
            return () => {
                var srcTab = _browserTabs.FirstOrDefault(t => t.Id == id);
                var item = CreateBrowserSpinoffItem(srcTab);   // 同 URL で新規 WebView2（再ペアレント空表示回避）
                if (srcTab is not null)
                    _ = CloseBrowserTabAsync(id);              // メインから元タブを除去＝移動
                return item;
            };
        return null;
    }
    // ===== 切り離しウィンドウ → メインの帯（戻す） =====

    /// <summary>
    /// ペインのヘッダー（帯の行そのもの）は、切り離したタブの<b>戻し先</b>でもある。切り離しウィンドウの
    /// タブを掴んでここへ落とすと、メインのタブとして戻る（Editor のタブは Editor の帯だけが受ける）。
    ///
    /// <para>受けるのは<b>そのペインの種類に合うタブだけ</b>——Diff やプレビューの複製にはメインの帯に
    /// 対応する居場所が無いので受けない（<see cref="DetachedItem.Return"/> が null）。運んでいるのが
    /// タブ（<see cref="DetachedPaneWindow.DetachDragFormat"/>）でなければ素通しするので、ペイン本体の
    /// ファイルドロップ（<c>OnEditorFileDrop</c> 等）は塞がない。</para>
    /// </summary>
    private void OnPaneHeaderTabDragOver(object sender, DragEventArgs e) {
        if (!CanReturnDetachedTab(sender, e))
            return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }
    private void OnPaneHeaderTabDrop(object sender, DragEventArgs e) {
        if (!CanReturnDetachedTab(sender, e))
            return;
        e.Handled = true;
        Detached.ReturnDraggedToMain();
        Activate();   // 戻した先はメイン窓＝前へ出す（掴んでいた切り離し窓が前面のままだと戻り先が見えない）
    }
    private bool CanReturnDetachedTab(object sender, DragEventArgs e)
        => e.Data.GetDataPresent(DetachedPaneWindow.DetachDragFormat)
           && sender is FrameworkElement { Tag: string tag }
           && Detached.DraggingReturn is { } ret
           && PaneTabKind(tag) == ret.Kind;
    /// <summary>ペインヘッダーの <c>Tag</c>（PaneKind の綴り）に対応するタブ種別（帯を持たないペインは null）。</summary>
    private static TabEntryKind? PaneTabKind(string paneTag) => paneTag switch {
        "Editor" => TabEntryKind.Editor,
        "Terminal" => TabEntryKind.Terminal,
        "Browser" => TabEntryKind.Browser,
        _ => null,
    };
    /// <summary>切り離しウィンドウから戻ってきたエディタタブを帯へ迎える。<b>実体はそのまま</b>——
    /// 引き出すときに <see cref="RemoveEditorTabForMove"/> が返した同じ <see cref="EditorTab"/> なので、
    /// タブ ID・コントロールに張った配線・未保存の本文・カーソル位置が切り離す前のまま続く。</summary>
    private void AdoptEditorTab(EditorTab tab) {
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, tab.PeekFilePath, tab.PeekIsModified, false);
        ActivateEditorTab(tab.Id);
        UpdateEditorTab(tab);   // 仮想ドキュメントの名前・変更マークは実体から引き直す
        FocusPane(PaneKind.Editor);
        SaveActiveWorkspaceSnapshot();
    }
    /// <summary>切り離しウィンドウから戻ってきたターミナルタブを帯へ迎える（生きたセッションのまま）。</summary>
    private void AdoptTerminalTab(TerminalTab tab) {
        _terminalTabs.Add(tab);
        _vm.Tabs.AddTerminalTab(tab.Id, tab.View.HeaderTitle, false);
        ActivateTerminalTab(tab.Id);
        FocusPane(PaneKind.Terminal);
        SaveActiveWorkspaceSnapshot();
    }
    private static Guid? ResolvePaneTabId(object originalSource) {
        for (var d = originalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: Guid id })
                return id;
        return null;
    }
    /// <summary>エディタタブをメインから外して<b>実体（<see cref="EditorTab"/>）ごと</b>返す（Dispose はしない
    /// ＝別ウィンドウへ移すため）。戻すときは同じ実体を <see cref="AdoptEditorTab"/> で帯へ戻す。</summary>
    private EditorTab? RemoveEditorTabForMove(Guid id) {
        var index = _editorTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return null;
        var tab = _editorTabs[index];
        var control = tab.Control;   // 未実体化なら実体化（生きたコントロールを移すため）
        var wasActive = _activeEditorTab?.Id == id;
        if (ReferenceEquals(_editorSupport.Source, tab)) {
            _editorSupportDebounceTimer?.Stop();
            DetachEditorSupportSource();
            _editorSupport.IsPinned = false;
            UpdateEditorSupportPinToggle();
        }
        ViewportTree.Detach(control);   // 視覚ツリーから外す（Dispose はしない＝別窓へ移す）
        if (ReferenceEquals(_previewEditorTab, tab))
            _previewEditorTab = null;
        _editorTabs.RemoveAt(index);
        _vm.Tabs.RemoveEditorTab(id);
        _editorViews?.RemoveTab(id);
        if (_editorTabs.Count == 0) {
            var newTab = CreateEditorTab();
            _editorTabs.Add(newTab);
            _vm.Tabs.AddEditorTab(newTab.Id, null, false, false);
            ActivateEditorTab(newTab.Id);
        } else {
            _editorViews?.RepairTabs(_editorTabs.Select(t => t.Id));
            if (wasActive)
                ActivateEditorTab(_editorTabs[Math.Min(index, _editorTabs.Count - 1)].Id);
            else {
                _editorViews?.Rebuild();
                if (_editorViews?.FocusedTabId is { } fid && _editorTabs.FirstOrDefault(t => t.Id == fid) is { } ft)
                    SetActiveEditorTab(ft);
            }
        }
        SaveActiveWorkspaceSnapshot();
        return tab;
    }
    /// <summary>ターミナルタブをメインから外して実体ごと返す（<c>CloseAsync</c> はしない＝別ウィンドウへ移す）。</summary>
    private TerminalTab? RemoveTerminalTabForMove(Guid id) {
        var index = _terminalTabs.FindIndex(t => t.Id == id);
        if (index < 0)
            return null;
        var tab = _terminalTabs[index];
        var wasActive = _activeTerminalTab?.Id == id;
        ViewportTree.Detach(tab.View);   // 視覚ツリーから外す（CloseAsync はしない＝別窓へ移す）
        _terminalTabs.RemoveAt(index);
        _vm.Tabs.RemoveTerminalTab(id);
        _terminalViews?.RemoveTab(id);
        ForgetTerminalActivity(id);
        if (_terminalTabs.Count == 0) {
            var startDir = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
            var newTab = CreateTerminalTab(startDir);
            _terminalTabs.Add(newTab);
            _vm.Tabs.AddTerminalTab(newTab.Id, "Terminal", false);
            ActivateTerminalTab(newTab.Id);
        } else {
            _terminalViews?.RepairTabs(_terminalTabs.Select(t => t.Id));
            if (wasActive)
                ActivateTerminalTab(_terminalTabs[Math.Min(index, _terminalTabs.Count - 1)].Id);
            else {
                _terminalViews?.Rebuild();
                if (_terminalViews?.FocusedTabId is { } fid && _terminalTabs.FirstOrDefault(t => t.Id == fid) is { } ft)
                    SetActiveTerminalTab(ft);
            }
        }
        SaveActiveWorkspaceSnapshot();
        return tab;
    }
}
