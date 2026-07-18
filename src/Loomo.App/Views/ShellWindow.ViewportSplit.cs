
namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ペイン内分割（vim 風 Ctrl+W v/s/q）と外観適用・PaneSplitView 実装</summary>
public partial class ShellWindow
{
    // ===== ペイン内分割の操作（Ctrl+W v/s/q） =====

    // フォーカス中ペインが内部分割しているなら、その分割ビューポートを1枚畳む。 畳めた（＝分割があった）場合のみ true。分割が無ければ false（呼び元はペイン非表示へフォールバック）。
    private bool CloseFocusedViewport()
    {
        switch (_focusedRegion?.Pane)
        {
            case PaneKind.Editor when _editorViews is { LeafCount: > 1 }:
                CloseEditorView();
                return true;
            case PaneKind.Terminal when _terminalViews is { LeafCount: > 1 }:
                CloseTerminalView();
                return true;
            default:
                return false;
        }
    }

    // Ctrl+W v/s/q を、フォーカス中ペイン（Editor / Terminal のみ）の分割操作へ振り分ける。
    private void HandleViewportSplitKey(Key key)
    {
        switch (_focusedRegion?.Pane)
        {
            case PaneKind.Editor:
                if (key == Key.V) SplitEditorView(SplitKind.Columns);
                else if (key == Key.S) SplitEditorView(SplitKind.Rows);
                else CloseEditorView();
                break;
            case PaneKind.Terminal:
                if (key == Key.V) SplitTerminalView(SplitKind.Columns);
                else if (key == Key.S) SplitTerminalView(SplitKind.Rows);
                else CloseTerminalView();
                break;
        }
    }

    // Editor ペインを分割し、新しいビューポートを隣に置く。filePath を指定した （:vsplit foo / :split foo 由来の）場合はそのファイルを開き、無指定なら フォーカス中タブと同じ内容を別コントロールへ複製する（真 vim 風）。
    private void SplitEditorView(SplitKind orientation, string? filePath = null)
    {
        if (_editorViews is null)
            return;
        var src = _editorViews.FocusedTabId is { } sid
            ? _editorTabs.FirstOrDefault(t => t.Id == sid)
            : _activeEditorTab;

        var openPath = ResolveEditorPath(filePath, src);

        var newTab = CreateEditorTab();
        _editorTabs.Add(newTab);
        _vm.Tabs.AddEditorTab(newTab.Id, openPath ?? src?.Control.FilePath, src?.Control.IsModified ?? false, false);

        if (openPath is not null)
        {
            newTab.Control.LoadFile(openPath);
        }
        else if (src is not null)
        {
            // 同じ内容をもう1つのコントロールで開く（保存済みファイルは読み直し、未保存はテキストを複製）。
            if (!string.IsNullOrWhiteSpace(src.Control.FilePath) && File.Exists(src.Control.FilePath) && !src.Control.IsModified)
                newTab.Control.LoadFile(src.Control.FilePath);
            else
                newTab.Control.SetText(src.Control.Text);
        }

        _editorViews.SplitFocused(orientation, newTab.Id);
        SetActiveEditorTab(newTab);
        UpdateEditorTab(newTab);
        SaveActiveWorkspaceSnapshot();
    }

    // エディタ由来のウィンドウ/タブ操作で渡されたパス（相対可）を、開ける実ファイルへ解決する。 絶対パス→ソースタブのあるフォルダ→ワークスペースルートの順に探し、存在しなければ null。
    private string? ResolveEditorPath(string? filePath, EditorTab? src)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;
        if (Path.IsPathRooted(filePath))
            return File.Exists(filePath) ? Path.GetFullPath(filePath) : null;

        var bases = new[]
        {
            src is { } s && !string.IsNullOrWhiteSpace(s.Control.FilePath)
                ? Path.GetDirectoryName(s.Control.FilePath)
                : null,
            _activeWorkspace?.RootPath,
            _terminal.CurrentDirectory,
        };
        foreach (var dir in bases)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.GetFullPath(Path.Combine(dir, filePath));
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // エディタの :tabnew 由来：ファイル指定があればそれを、無ければ空タブを新規エディタタブで開く。
    private async Task OpenEditorTabFromEditorAsync(string? filePath)
    {
        var openPath = ResolveEditorPath(filePath, _activeEditorTab);
        if (openPath is not null)
        {
            await OpenFileInNewEditorTabAsync(openPath);
            return;
        }

        var tab = CreateEditorTab();
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, null, false, false);
        ActivateEditorTab(tab.Id);
        UpdateEditorTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    // エディタの gt / gT 由来：アクティブなエディタタブを巡回切り替えする。
    private void CycleEditorTab(int step)
    {
        if (_editorTabs.Count <= 1)
            return;
        var index = _activeEditorTab is { } active ? _editorTabs.FindIndex(t => t.Id == active.Id) : 0;
        if (index < 0)
            index = 0;
        var count = _editorTabs.Count;
        var next = ((index + step) % count + count) % count;
        ActivateEditorTab(_editorTabs[next].Id);
    }

    // エディタの :tabclose 由来：アクティブなエディタタブを閉じる。
    private void CloseActiveEditorTab()
    {
        if (_activeEditorTab is not { } active)
            return;
        CloseEditorTab(active.Id);
        SaveActiveWorkspaceSnapshot();
    }

    // Editor のフォーカス中ビューポートを畳む（タブ自体は閉じない）。
    private void CloseEditorView()
    {
        if (_editorViews?.CloseFocused() != true)
            return;
        if (_editorViews.FocusedTabId is { } id && _editorTabs.FirstOrDefault(t => t.Id == id) is { } tab)
            SetActiveEditorTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    // Terminal ペインを分割し、同じ作業ディレクトリの新しいターミナルを隣のビューポートに置く。
    private void SplitTerminalView(SplitKind orientation)
    {
        if (_terminalViews is null)
            return;
        var src = _terminalViews.FocusedTabId is { } sid
            ? _terminalTabs.FirstOrDefault(t => t.Id == sid)
            : _activeTerminalTab;
        var cwd = src?.View.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            cwd = _activeWorkspace?.RootPath ?? _terminal.CurrentDirectory;

        var newTab = CreateTerminalTab(cwd);
        _terminalTabs.Add(newTab);
        _vm.Tabs.AddTerminalTab(newTab.Id, $"Terminal {CurrentTerminalWorkspace.NextTabNumber++}", false);

        _terminalViews.SplitFocused(orientation, newTab.Id);
        SetActiveTerminalTab(newTab);
        SaveActiveWorkspaceSnapshot();
    }

    // Terminal のフォーカス中ビューポートを畳む（タブ自体は閉じない）。
    private void CloseTerminalView()
    {
        if (_terminalViews?.CloseFocused() != true)
            return;
        if (_terminalViews.FocusedTabId is { } id && _terminalTabs.FirstOrDefault(t => t.Id == id) is { } tab)
            SetActiveTerminalTab(tab);
        SaveActiveWorkspaceSnapshot();
    }

    private TerminalTab CreateTerminalTab(string startDirectory, Guid? requestedId = null)
    {
        var view = new TerminalTabView("pwsh.exe", startDirectory)
        {
            // 初回セッションの自動起動（ConPTY・非同期）が完了時にフォーカスを奪わないようにする。
            // これが true だと、ワークスペース復元の最後に舞台へ入れたフォーカスを
            // 約1秒後のセッション起動が横取りする（sk0ya.Terminal.Controls 1.0.9）。
            AutoFocusOnStart = false,
        };
        _appearance.ApplyTerminalAppearance(view);
        var tab = new TerminalTab(requestedId ?? Guid.NewGuid(), view);
        view.HeaderTitleChanged += (_, title) => UpdateTerminalTab(tab, title);
        // ターミナル本文の URL クリックを Loomo で受け、http/https は内蔵ブラウザへ振り分ける
        // （sk0ya.Terminal.Controls 1.0.10）。
        view.HyperlinkActivated += OnTerminalLinkActivated;
        // 右クリックメニューへ「AIに聞く」「ブラウザで調べる」を追加する（選択時のみ・sk0ya.Terminal.Controls 1.0.19）。
        view.ContextMenuBuilding += OnTerminalContextMenuBuilding;
        HookTerminalActivity(tab);
        return tab;
    }

    // 空（または即時使用）のエディタタブを作る。コントロールは EditorTab.Control の 初回アクセスで実体化されるが、ここで作るタブは生成直後に LoadFile 等で使われるため実質その場で実体化する。
    private EditorTab CreateEditorTab(Guid? requestedId = null) =>
        new(requestedId ?? Guid.NewGuid()) { Realizer = RealizeEditorControl };

    // 保存済みスナップショットだけを持つ未実体化タブを作る（起動時の遅延復元用）。コントロールは アクティブ化・本文取得で初めて生成され、その際 EditorTab.Pending から本文が復元される。
    private EditorTab CreatePendingEditorTab(EditorTabSnapshot snapshot) =>
        new(snapshot.Id == Guid.Empty ? Guid.NewGuid() : snapshot.Id)
        {
            Realizer = RealizeEditorControl,
            Pending = snapshot
        };

    // EditorTab.Control 初回アクセス時の実体化本体。コントロールを生成・配線し、 EditorTab.SetControl で先に確定してから Pending を復元する（LoadFile→BufferChanged が Control へ再入しても無限再帰しない）。
    private void RealizeEditorControl(EditorTab tab)
    {
        var control = BuildEditorControl(tab);
        tab.SetControl(control);
        if (tab.Pending is { } snapshot)
        {
            WorkspaceSessionCoordinator.RestoreEditor(control, snapshot);
            tab.Pending = null;
        }
    }

    // エディタコントロールごとに、その LSP マネージャ（LspManagerFactoryRetain で 遅延生成されたもの）を保持する。EditorSupport のコード構造／呼び出し解析 （UpdateCodeEditorSupportAsync）から参照する。コントロールが GC されれば エントリも自動で消えるよう ConditionalWeakTable{TKey,TValue} を使う。値は factory がファイル初回オープン時に遅延実行されるまで null（StrongBox{T} で共有）。
    private readonly ConditionalWeakTable<VimEditorControl, StrongBox<IEditorLspManager?>> _editorLspManagers = new();

    // 指定タブのエディタコントロールに紐づく LSP マネージャを返す（未実体化／未オープンなら null）。
    private IEditorLspManager? GetLspManager(EditorTab tab)
    {
        if (!tab.IsRealized)
            return null; // コントロール未実体化＝LSP はまだ存在しない
        return _editorLspManagers.TryGetValue(tab.Control, out var box) ? box.Value : null;
    }

    private VimEditorControl BuildEditorControl(EditorTab tab)
    {
        // LSP マネージャは factory がファイル初回オープン時に遅延生成する。生成物をこの箱経由で受け取り、
        // コントロール単位で retain する（EditorSupport のコード構造解析が参照するため）。
        var lspBox = new StrongBox<IEditorLspManager?>(null);
        // GitServiceFactory を渡すと、エディタが行の差分（追加/変更/削除）をガター（行番号脇）に
        // マーク表示し、ステータスバーにブランチ名を出す。読込/保存/編集のたびに自動で再計算される
        // （RefreshGitDiff はコントロール内部で発火）。未指定だと NullEditorGitService となり無効。
        // LspManagerFactory を渡すと補完・診断・定義ジャンプ等の LSP 機能が有効になる（対象言語の
        // 言語サーバーが PATH に必要）。拡張子→サーバーの対応はエディタ側 LspServerRegistry が所有・
        // 永続化し、ユーザーは :LspAdd/:LspRemove/:LspList/:LspReset で追加削除する（Loomo は持たない）。
        var control = new VimEditorControl(new VimEditorControlOptions
        {
            GitServiceFactory = () => new GitDiffProvider(),
            LspManagerFactory = dispatcher =>
            {
                var manager = new LspManager(dispatcher);
                lspBox.Value = manager;
                return manager;
            }
        })
        {
            VimEnabled = _settings.Vim.Enabled,
            Visibility = Visibility.Collapsed
        };
        _appearance.ApplyEditorOptions(control);
        _appearance.ApplyEditorAppearance(control);
        // 分割時もステータスバーを1つに集約する（sk0ya.Editor.Controls 1.0.5 の共有ステータスバー機能）。
        // 各コントロールの内蔵バーは隠れ、フォーカス中エディタの状態だけが下端の共有バーへ流れる。
        control.SetSharedStatusBar(EditorSharedStatusBar);
        control.BufferChanged += (_, _) =>
        {
            UpdateEditorTab(tab);
            RecordTrailEdit(tab);
            if (ReferenceEquals(_editorSupport.Source, tab))
                ScheduleEditorSupportUpdate();
        };
        control.SaveRequested += (_, _) =>
        {
            QueueEditorTabUpdate(tab);
            if (ReferenceEquals(_editorSupport.Source, tab))
                ScheduleEditorSupportUpdate();
        };
        // エディタからの明示的なプレビュー要求は、EditorSupport ペインを「手動で開いた」扱いにする。
        control.MarkdownPreviewRequested += async (_, _) => await OpenEditorSupportAsync(tab);
        // エディタ本文中のURLを Ctrl+Click / gx で開く操作（sk0ya.Editor.Controls 1.0.6）は、
        // OS の既定ブラウザではなく Loomo 内蔵のブラウザペインで開く（Handled=true で既定動作を抑止）。
        control.LinkClicked += OnEditorLinkClicked;
        control.FileLinkClicked += OnEditorFileLinkClicked;
        // 使用箇所一覧（Find References / gr）：エディタは結果を表示せず FindReferencesResult を
        // 発火するだけなので、ホストが受けてポップアップに一覧表示する（ShellWindow.References.cs）。
        control.FindReferencesResult += OnEditorFindReferencesResult;
        // 右クリックメニューへ「AIに聞く」「ブラウザで調べる」を追加する（選択時のみ・sk0ya.Editor.Controls 1.0.19）。
        control.ContextMenuBuilding += OnEditorContextMenuBuilding;
        // blame 左カラム（:Gblame 表示中）の行クリック：該当コミットの差分を Diff ペインで開き、
        // そのファイルを選択してクリック行（コミット時点の行番号）までスクロールする
        // （sk0ya.Editor.Controls 1.0.40。ハッシュを解析できない注釈＝カスタム形式では何もしない）。
        // 左クリックは従来どおり Diff ペインへ直行する（右クリックでは Diff / Git 履歴を選べる。
        // ShellWindow.SelectionActions.cs の AddBlameCommitMenuItems と実処理を共有する）。
        control.BlameCommitClicked += (_, e) => ShowBlameCommitDiff(control, e.Blame);

        // エディタ内の Vim ウィンドウ/タブ操作（:vsplit / :split / :tabnew / gt / gT / :tabclose / :close）を、
        // ホスト側の分割・タブ実装へ橋渡しする
        // （sk0ya.Editor.Controls 1.0.5 の公開API：エディタはイベントを発火するだけで、レイアウトはホストが担う）。
        // イベントはフォーカス中のエディタから発火するので、その時点のフォーカス領域に対して作用させる。
        // ※ Ctrl+W 系のウィンドウ移動（h/j/k/l/w）はシェルの OnPaneNavKey が Window の PreviewKeyDown で
        //   先取りして処理するため WindowNavRequested は購読しない（購読しても発火しないデッド配線になる）。
        control.SplitRequested += (_, e) => SplitEditorView(e.Vertical ? SplitKind.Columns : SplitKind.Rows, e.FilePath);
        control.NewTabRequested += async (_, e) => await OpenEditorTabFromEditorAsync(e.FilePath);
        control.NextTabRequested += (_, _) => CycleEditorTab(+1);
        control.PrevTabRequested += (_, _) => CycleEditorTab(-1);
        control.CloseTabRequested += (_, _) => CloseActiveEditorTab();
        control.WindowCloseRequested += (_, _) => CloseEditorView();
        // デバッグ：ブレークポイント列を有効化し、トグル/同期/実行行ハイライトを配線する。
        WireEditorForDebug(control);
        // LSP マネージャ（遅延生成）を保持する箱をこのコントロールに紐づける（GetLspManager から引く）。
        _editorLspManagers.AddOrUpdate(control, lspBox);
        return control;
    }

    private void ApplyVimEnabledToOpenEditorTabs()
    {
        // 未実体化タブは実体化しない（生成時に現在の Vim 設定が適用されるため不要）。
        foreach (var tab in _editorTabs)
            if (tab.IsRealized)
                tab.Control.VimEnabled = _settings.Vim.Enabled;
    }

    private void ApplyEditorSettingsToOpenEditorTabs()
    {
        foreach (var tab in _editorTabs)
        {
            if (!tab.IsRealized) continue;
            _appearance.ApplyEditorOptions(tab.Control);
        }
    }

    // 設定画面のエディタ項目（AiSettings.Editor）を1つの VimEditorControl に適用する。真偽値の大半は VimOptions 直下のフィールドで コントロール側の依存関係プロパティではないため、ExecuteCommand("set ...")（Vim の :set 相当）経由で適用する — エンジンが発する OptionsChanged イベントが内部の 再描画（UpdateAll）を呼ぶので、生成直後・タブ実体化後のどちらでも即座に反映される。 EditorSettings.HighlightWhitespace だけは :set に無い専用フィールドのため 直接代入＋UIElement.InvalidateVisual で反映する。
    private void ApplyAppearanceToOpenTabs()
    {
        // 未実体化タブは実体化しない（生成時に現在の外観が適用されるため不要）。
        foreach (var tab in _editorTabs)
            if (tab.IsRealized)
                _appearance.ApplyEditorAppearance(tab.Control);
        foreach (var tab in _terminalTabs)
            _appearance.ApplyTerminalAppearance(tab.View);
        if (_editorSupport.Source is not null)
            ScheduleEditorSupportUpdate();
    }

    private void QueueEditorTabUpdate(EditorTab tab)
    {
        _ = tab.Control.Dispatcher.BeginInvoke(new Action(() => UpdateEditorTab(tab)));
    }

    private void OnTabStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.ScrollableWidth <= 0)
            return;

        var nextOffset = Math.Clamp(
            scrollViewer.HorizontalOffset - e.Delta,
            0,
            scrollViewer.ScrollableWidth);

        scrollViewer.ScrollToHorizontalOffset(nextOffset);
        e.Handled = true;
    }
}
