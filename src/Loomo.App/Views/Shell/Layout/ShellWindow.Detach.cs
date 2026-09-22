using System.Collections.ObjectModel;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン項目の別ウィンドウ切り離し。Editor は同一ファイルの複製＋双方向テキスト同期、 Terminal/Browser は同期なしの新規スピンオフ。ウィンドウ管理・タブ結合は <see cref="DetachedWindowManager"/>。 状態はワークスペースのスナップショットへ保存し、切替・再起動時に復元する。</summary>
public partial class ShellWindow {
    private DetachedWindowManager? _detached;
    private DetachedWindowManager Detached => _detached ??= new DetachedWindowManager(this, () => SaveActiveWorkspaceSnapshot());
    private PaneTabDragInteractionController? _paneTabDragInteraction;
    private PaneTabDragInteractionController PaneTabDragInteraction
        => _paneTabDragInteraction ??= new PaneTabDragInteractionController(
            this, MovePaneTab, StartPaneTabTearOff, () => SaveActiveWorkspaceSnapshot());
    private PaneTabOverflowPresenter? _paneTabOverflowPresenter;
    private PaneTabOverflowPresenter PaneTabOverflow
        => _paneTabOverflowPresenter ??= new PaneTabOverflowPresenter(
            TabOverflowPopup, TabOverflowPopupList, _vm.Tabs, ActivatePaneTab);
    private DetachedBrowserLifecycleController? _detachedBrowserLifecycle;
    private DetachedBrowserLifecycleController DetachedBrowserLifecycle
        => _detachedBrowserLifecycle ??= new DetachedBrowserLifecycleController(
            Dispatcher, () => CreateBrowserView(), item => _detached?.AllItems.Contains(item) == true,
            OpenUrlInDetachedWindow, DefaultBrowserUrl);
    private DetachedItemSnapshot? CaptureDetachedItem(DetachedItem item)
        => DetachedItemStateCoordinator.Capture(item);

    private DetachedItem? RestoreDetachedItem(DetachedItemSnapshot snapshot) {
        return DetachedItemStateCoordinator.Restore(
            snapshot,
            path => _editorTabs.FirstOrDefault(tab => string.Equals(
                tab.PeekFilePath, path, StringComparison.OrdinalIgnoreCase))?.Id,
            TryCreateEditorMirrorItem,
            CreateRestoredEditorMove,
            CreateRestoredEditorSupportMirror,
            CreateTerminalSpinoffItem,
            CreateBrowserSpinoffItem);
    }

    private DetachedItem CreateRestoredEditorMove(DetachedItemSnapshot snapshot) {
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

    private DetachedItem? CreateRestoredEditorSupportMirror(string path) {
        var source = _editorTabs.FirstOrDefault(tab => string.Equals(
            tab.PeekFilePath, path, StringComparison.OrdinalIgnoreCase));
        if (source is null) return null;
        var view = new DetachedEditorSupportView(
            _editorSupportResolver, _editorSupport.Pipeline, _editorSupport.WebView.ViewFactory,
            _settings, _workspace, source.Control);
        var item = new DetachedItem(DetachKind.EditorSupportMirror, $"Preview: {Path.GetFileName(path)}", view, dispose: view.Dispose);
        view.TitleChanged += (_, title) => item.Title = title;
        AttachEditorSupportMirrorLinks(view);
        return item;
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
    private DetachedItem? TryCreateEditorMirrorItem(Guid sourceTabId)
        => DetachedEditorMirrorCoordinator.TryCreate(
            _editorTabs, sourceTabId, () => CreateEditorTab(),
            (editor, path) => LoadEditorFile(editor, path),
            path => _tabIcons.GetFileIcon(path), AdoptEditorTab);
    private DetachedItem CreateTerminalSpinoffItem(TerminalTab? sourceTab)
        => CreateTerminalSpinoffItem(sourceTab?.View.WorkingDirectory);
    private DetachedItem CreateTerminalSpinoffItem(string? sourceDirectory) {
        var cwd = DetachedLaunchTargetPolicy.ResolveTerminalWorkingDirectory(
            sourceDirectory, _activeWorkspace?.RootPath, _terminal.CurrentDirectory);
        var view = new TerminalTabView("pwsh.exe", cwd) { AutoFocusOnStart = false };
        _appearance.ApplyTerminalAppearance(view);
        // メインの帯へ戻すときはここで張った見出し追従を外し、メインのタブとしての配線を張り直す
        // （タブの実体＝生きたセッションはそのまま運ぶ）。
        return DetachedTerminalLifecycleController.CreateItem(
            DetachKind.TerminalSpinoff, view, _tabIcons.GetTerminalIcon(),
            () => HookTerminalTab(new TerminalTab(Guid.NewGuid(), view)), AdoptTerminalTab);
    }
    private DetachedItem CreateBrowserSpinoffItem(BrowserTab? sourceTab)
        => CreateBrowserSpinoffItem(BrowserUrlOf(sourceTab));
    /// <summary>切り離したブラウザ。<see cref="DetachedItem.Content"/> は差し替えられないので、
    /// <b>器（Grid）を挟んで</b>中の WebView2 だけを作り直せるようにする——ブラウザプロセスが落ちたら
    /// コントロールごと作り直すため（§21.5.3。共有プロファイルなので他インスタンスの巻き添えでも落ちる）。
    /// 器はもう1つ、<b>別の切り離し窓へタブを移したとき</b>の作り直しにも効く（<see cref="ReparentRebuild"/>）。</summary>
    private DetachedItem CreateBrowserSpinoffItem(string? sourceUrl) {
        // 行き先は器より長生きさせる（実体を作り直しても、いま見ているページを見失わないため）。
        var address = new SpinoffBrowserAddress(
            DetachedLaunchTargetPolicy.InitialBrowserAddress(sourceUrl, DefaultBrowserUrl));
        var host = new Grid();
        address.AttachTo(host);   // スナップショット保存が実体より先に走っても行き先を見失わない
        var view = CreateBrowserView();
        view.Visibility = Visibility.Visible;
        host.Children.Add(view);
        DetachedItem? item = null;
        item = new DetachedItem( DetachKind.BrowserSpinoff, "Browser", host, _tabIcons.GetBrowserDefaultIcon(), dispose: () => DetachedBrowserLifecycleController.DisposeContent(host)) {
            // 戻すときは<b>作り直す</b>——WebView2（コンポジション版）は窓をまたいで載せ替えると
            // コンポジタが元の窓に残って空表示になる（引き出すときも同じ理由で新規生成している）。
            Return = new DetachReturn(TabEntryKind.Browser, () => {
                address.Note(DetachedBrowserLifecycleController.CurrentUrl(host));   // 行き先は手放す前に控える（捨てた器から読まない）
                DetachedBrowserLifecycleController.DisposeContent(host);
                _ = CreateBrowserTabAsync(address.Value);
                FocusPane(PaneKind.Browser);
            })
        };
        // 切り離し窓から<b>別の切り離し窓へ</b>タブを移したときも同じ——載せ替えただけでは空表示に
        // なるので、いま見ている URL のまま器の中身を作り直す。ここが抜けていて、窓をまたいで移した
        // ブラウザのタブが真っ白になっていた（引き出す・戻すの両端だけ手当てされていた）。
        ReparentRebuild.Watch(host, () => DetachedBrowserLifecycle.Rebuild(host, item!, address));
        _ = DetachedBrowserLifecycle.RealizeAsync(host, view, address, item);
        return item;
    }
    /// <summary>切り離しブラウザの器がいま見ている URL（まだ生成前・生成に失敗していれば null）。
    /// 読み方は本体ペインの <see cref="BrowserUrlOf"/> と同じ <see cref="WebViewSafe.TryUrl"/>——ここは
    /// 器の中身を<b>作り直す</b>ときの行き先なので、ラッパーの <c>Source</c> を読んで取り残された古い値を
    /// 掴むと、見ていたページが黙って1つ前へ戻る。</summary>
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
            () => { try { view.Dispose(); } finally { vm.Dispose(); } });
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
    private void OnPaneTabPreviewMouseDown(object sender, MouseButtonEventArgs e)
        => PaneTabDragInteraction.OnPreviewMouseDown(e);
    private void OnPaneTabPreviewMouseMove(object sender, MouseEventArgs e)
        => PaneTabDragInteraction.OnPreviewMouseMove(sender, e);
    /// <summary>タブ帯上のドラッグ並べ替え。コードビハインドの実体リスト（<see cref="_editorTabs"/> 等、
    /// タブ切替・ワークスペース復元が位置参照する）と ViewModel 側の <see cref="TabsViewModel"/> 表示用
    /// コレクションの両方を同じ並びに保つ。</summary>
    private void MovePaneTab(Guid draggedId, Guid targetId) {
        if (PaneTabDragPolicy.TryMoveTabAndEntry(_editorTabs, _vm.Tabs.EditorTabs, t => t.Id, draggedId, targetId))
            return;
        if (PaneTabDragPolicy.TryMoveTabAndEntry(_terminalTabs, _vm.Tabs.TerminalTabs, t => t.Id, draggedId, targetId))
            return;
        PaneTabDragPolicy.TryMoveTabAndEntry(_browserTabs, _vm.Tabs.BrowserTabs, t => t.Id, draggedId, targetId);
    }
    /// <summary>タブ帯の「▾」：あふれて見えなくなったタブも含む全件を一覧表示し、クリックで直接アクティブ化する。</summary>
    private void OnTabOverflowClick(object sender, RoutedEventArgs e) {
        if (sender is FrameworkElement { Tag: string kind } button)
            PaneTabOverflow.Show(
                button, kind, (Style)FindResource("BranchMenuItem"),
                (Brush)FindResource("FgDim"), (Brush)FindResource("Accent"), UiFontManager.Scaled(12));
    }
    private void ActivatePaneTab(TabEntryViewModel tab) {
        switch (tab.Kind) {
            case TabEntryKind.Terminal: ActivateTerminalTab(tab.Id); break;
            case TabEntryKind.Editor: ActivateEditorTab(tab.Id); break;
            case TabEntryKind.Browser: ActivateBrowserTab(tab.Id); break;
        }
    }
    /// <summary>ペインのタブを帯の外へ引き出すドラッグ。切り離しウィンドウ側と同じ演出——運んでいるタブを
    /// カーソルに付け（<see cref="TabDragGhost"/>）、元のタブは薄く残す（設計書 §21.4 の「タブが動く」の続き）。</summary>
    private void StartPaneTabTearOff(Guid id, UIElement? source) {
        if (source is null || BuildTearOffFactory(id) is not { } factory)
            return;
        PaneTabTearOffPresenter.Start(this, source, FindPaneTabEntry(id), factory, Detached);
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
                return DetachedTerminalLifecycleController.CreateItem(
                    DetachKind.TerminalMove, tab.View, _tabIcons.GetTerminalIcon(), () => tab, AdoptTerminalTab);
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
           && PaneTabDragPolicy.CanReturnToPane(tag, ret.Kind);
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
    private static Guid? ResolvePaneTabId(object originalSource)
        => PaneTabDragInteractionController.ResolveTabId(originalSource);
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
        PaneTabTransferCoordinator.CompleteRemoval(
            _editorTabs, tab => tab.Id, _editorViews, index, wasActive, id => ActivateEditorTab(id),
            id => { if (_editorTabs.FirstOrDefault(t => t.Id == id) is { } focused) SetActiveEditorTab(focused); },
            () => {
                var newTab = CreateEditorTab();
                _editorTabs.Add(newTab);
                _vm.Tabs.AddEditorTab(newTab.Id, null, false, false);
                ActivateEditorTab(newTab.Id);
            });
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
        PaneTabTransferCoordinator.CompleteRemoval(
            _terminalTabs, tab => tab.Id, _terminalViews, index, wasActive, id => ActivateTerminalTab(id),
            id => { if (_terminalTabs.FirstOrDefault(t => t.Id == id) is { } focused) SetActiveTerminalTab(focused); },
            () => {
                var startDir = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;
                var newTab = CreateTerminalTab(startDir);
                _terminalTabs.Add(newTab);
                _vm.Tabs.AddTerminalTab(newTab.Id, "Terminal", false);
                ActivateTerminalTab(newTab.Id);
            });
        SaveActiveWorkspaceSnapshot();
        return tab;
    }
}
