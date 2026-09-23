using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークスペースセッションの復元判断と表示モデル変換。</summary>
public static class WorkspaceSessionCoordinator
{
    /// <summary>ワークスペースごとのタブ状態を初回だけ生成してキャッシュする。</summary>
    internal static TWorkspace GetOrCreateWorkspace<TWorkspace>(
        Dictionary<Guid, TWorkspace> workspaces,
        Guid workspaceId,
        Func<TWorkspace> create)
    {
        if (workspaces.TryGetValue(workspaceId, out var workspace))
            return workspace;

        workspace = create();
        workspaces.Add(workspaceId, workspace);
        return workspace;
    }

    /// <summary>使わなくなったワークスペースのタブ実体を閉じ、キャッシュから取り除く。</summary>
    internal static async Task DisposeWorkspaceTabsAsync(
        Guid workspaceId,
        Dictionary<Guid, TerminalWorkspaceTabs> terminalWorkspaces,
        Dictionary<Guid, EditorWorkspaceTabs> editorWorkspaces,
        Dictionary<Guid, BrowserWorkspaceTabs> browserWorkspaces)
    {
        if (terminalWorkspaces.Remove(workspaceId, out var terminal))
            foreach (var tab in terminal.Tabs)
                await tab.View.CloseAsync();
        if (browserWorkspaces.Remove(workspaceId, out var browser))
            foreach (var tab in browser.Tabs)
                tab.View.Dispose();
        if (editorWorkspaces.Remove(workspaceId, out var editor))
            foreach (var tab in editor.Tabs)
                if (tab.IsRealized)
                    tab.Control.Dispose();
    }

    /// <summary>スナップショットに記録されたプライマリ／追加フォルダーを復元用の順序で返す。</summary>
    public static List<string> WorkspaceFolders(WorkspaceSnapshot workspace)
    {
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspace.RootPath))
            folders.Add(workspace.RootPath);
        folders.AddRange(workspace.AdditionalFolders
            .Select(folder => folder.FolderPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)));
        return folders;
    }

    /// <summary>保存対象の作業フォルダー。存在しない場合は現在の既定フォルダーへ戻す。</summary>
    public static string? ResolveWorkingDirectory(string? workingDirectory, string? fallbackDirectory)
        => Directory.Exists(workingDirectory) ? workingDirectory : fallbackDirectory;

    /// <summary>旧 single-editor 鏡へ使うタブ。アクティブ ID が無ければ先頭のタブを使う。</summary>
    public static EditorTabSnapshot? SelectActiveEditorSnapshot(
        IReadOnlyList<EditorTabSnapshot> snapshots, Guid? activeTabId)
        => snapshots.FirstOrDefault(snapshot => snapshot.Id == activeTabId)
           ?? snapshots.FirstOrDefault();

    /// <summary>保存済みタブが無い旧データでは互換スナップショットを使い、復元対象とアクティブ位置を決める。</summary>
    public static WorkspaceTabRestorePlan<TSnapshot> ResolveTabRestorePlan<TSnapshot>(
        IReadOnlyList<TSnapshot> saved, TSnapshot fallback, Func<TSnapshot, bool> isActive)
    {
        IReadOnlyList<TSnapshot> snapshots = saved.Count == 0 ? new[] { fallback } : saved;
        var activeIndex = 0;
        for (var index = 0; index < snapshots.Count; index++)
            if (isActive(snapshots[index]))
            {
                activeIndex = index;
                break;
            }
        return new(snapshots, activeIndex);
    }

    public static WorkspaceTabRestorePlan<TerminalTabSnapshot> ResolveTerminalTabRestorePlan(
        WorkspaceSnapshot workspace)
        => ResolveTabRestorePlan(workspace.TerminalTabs, new TerminalTabSnapshot
        {
            WorkingDirectory = workspace.Terminal.WorkingDirectory,
            Title = workspace.Terminal.Title ?? "Terminal",
            IsActive = true,
        }, snapshot => snapshot.IsActive);

    public static WorkspaceTabRestorePlan<EditorTabSnapshot> ResolveEditorTabRestorePlan(
        WorkspaceSnapshot workspace)
        => ResolveTabRestorePlan(workspace.EditorTabs, new EditorTabSnapshot
        {
            FilePath = workspace.Editor.FilePath,
            Text = workspace.Editor.Text,
            IsModified = workspace.Editor.IsModified,
            IsActive = true,
        }, snapshot => snapshot.IsActive);

    public static WorkspaceTabRestorePlan<BrowserTabSnapshot> ResolveBrowserTabRestorePlan(
        WorkspaceSnapshot workspace, string defaultUrl)
        => ResolveTabRestorePlan(workspace.BrowserTabs, new BrowserTabSnapshot
        {
            Url = defaultUrl,
            Title = null,
            IsActive = true,
        }, snapshot => snapshot.IsActive);

    /// <summary>端末タブと旧 single-terminal 鏡を永続化形式へ写す。</summary>
    internal static void CaptureTerminalTabs(WorkspaceSnapshot snapshot,
        IEnumerable<TerminalTab> tabs, Guid? activeTabId, string? fallbackDirectory)
    {
        var tabList = tabs.ToList();
        snapshot.TerminalTabs = tabList.Select(tab => new TerminalTabSnapshot
        {
            Id = tab.Id,
            WorkingDirectory = ResolveWorkingDirectory(tab.View.WorkingDirectory, fallbackDirectory),
            Title = tab.View.HeaderTitle,
            IsActive = tab.Id == activeTabId,
        }).ToList();

        var active = tabList.FirstOrDefault(tab => tab.Id == activeTabId) ?? tabList.FirstOrDefault();
        if (active is null)
            return;
        snapshot.Terminal.WorkingDirectory = ResolveWorkingDirectory(active.View.WorkingDirectory, fallbackDirectory);
        snapshot.Terminal.Title = active.View.HeaderTitle;
    }

    /// <summary>仮想ドキュメントを除いたエディタタブと旧 single-editor 鏡を永続化形式へ写す。</summary>
    internal static void CaptureEditorTabs(WorkspaceSnapshot snapshot,
        IEnumerable<EditorTab> tabs, Guid? activeTabId)
    {
        snapshot.EditorTabs = tabs.Where(tab => !tab.PeekIsVirtual)
            .Select(tab => CaptureEditorTab(tab, activeTabId))
            .ToList();

        // 鏡は保存済みタブから引き、本文の複製を余分に作らない。
        var active = SelectActiveEditorSnapshot(snapshot.EditorTabs, activeTabId);
        if (active is null)
            return;
        snapshot.Editor.FilePath = active.FilePath;
        snapshot.Editor.Text = active.Text;
        snapshot.Editor.IsModified = active.IsModified;
    }

    /// <summary>プレビュー専用ブラウザータブを除いて保存形式へ写す。</summary>
    public static BrowserTabSnapshot? CaptureBrowserTab(
        Guid id, string? url, string? title, bool isActive)
        => EditorSupportNavigationService.IsPreviewUrl(url)
            ? null
            : new BrowserTabSnapshot { Id = id, Url = url, Title = title, IsActive = isActive };

    /// <summary>一時プレビューを除いたブラウザータブ一覧を保存形式へ写す。</summary>
    internal static List<BrowserTabSnapshot> CaptureBrowserTabs(
        IEnumerable<BrowserTab> tabs, Guid? activeTabId)
        => tabs.Select(tab => CaptureBrowserTab(
                tab.Id,
                BrowserDisplayMapper.CurrentUrl(tab.View.TryUrl(), tab.PendingUrl),
                tab.CurrentTitle,
                tab.Id == activeTabId))
            .OfType<BrowserTabSnapshot>()
            .ToList();

    /// <summary>現在のペイン木から、最大化前に保存する木か表示中の木をスナップショットへ写す。</summary>
    internal static void CapturePaneLayout(
        WorkspaceSnapshot snapshot,
        bool spanMaximized,
        PaneNode? savedRoot,
        PaneNode? currentRoot,
        Action captureCurrentSizes,
        Func<PaneNode, PaneNodeSnapshot> toSnapshot)
    {
        var useSavedRoot = spanMaximized && savedRoot is not null;
        if (!useSavedRoot)
            captureCurrentSizes();
        var root = useSavedRoot ? savedRoot : currentRoot;
        snapshot.PaneLayout = root is null ? null : toSnapshot(root);
    }

    /// <summary>名前付き配置と表示中レイアウトの選択情報をスナップショットへ写す。</summary>
    internal static void CaptureLayouts(
        WorkspaceSnapshot snapshot,
        IEnumerable<SavedLayout> layouts,
        PaneNodeSnapshot? scratchLayout,
        int activeLayoutIndex,
        bool layoutDirty,
        ViewportNodeSnapshot? editorViewLayout,
        ViewportNodeSnapshot? terminalViewLayout)
    {
        snapshot.Layouts = layouts.Select(layout => new SavedLayout { Name = layout.Name, Tree = layout.Tree }).ToList();
        snapshot.ScratchLayout = scratchLayout;
        snapshot.ActiveLayoutIndex = activeLayoutIndex;
        snapshot.LayoutDirty = layoutDirty;
        snapshot.EditorViewLayout = editorViewLayout;
        snapshot.TerminalViewLayout = terminalViewLayout;
    }

    /// <summary>表示モード、ステージ、ドック、選択位置をスナップショットへまとめる。</summary>
    internal static void CaptureDisplayState(
        WorkspaceSnapshot snapshot,
        DisplayMode mode,
        IEnumerable<PaneKind> enabledSessions,
        WingTab activeWingTab,
        StageSnapshot stage,
        DockSnapshot dock,
        bool stageActive,
        PaneKind stagePane,
        PaneKind? focusedPane)
    {
        snapshot.Mode = mode;
        snapshot.EnabledSessions = enabledSessions.ToList();
        snapshot.ActiveWingTab = activeWingTab;
        snapshot.Stage = stage;
        snapshot.Dock = dock;
        // サイドバーへ一時的にフォーカスしていても、最後のメインペインという現在地は失わない。
        snapshot.ActivePane = ResolveActivePane(stageActive, stagePane, focusedPane, snapshot.ActivePane);
    }

    /// <summary>表示中のステージ状態を永続化用スナップショットへ写す。</summary>
    public static StageSnapshot CaptureStage(
        bool isActive, PaneKind stagePane, bool overview, double wingWidth, bool wingCollapsed)
        => new()
        {
            IsActive = isActive,
            Pane = isActive ? stagePane : null,
            Overview = isActive && overview,
            WingWidth = wingWidth,
            WingCollapsed = wingCollapsed,
        };

    /// <summary>復元するアクティブペイン。ステージ／現在のフォーカスがなければ保存値を使う。</summary>
    public static PaneKind? ResolveActivePane(
        bool stageActive, PaneKind stagePane, PaneKind? focusedPane, PaneKind? savedPane)
        => stageActive ? stagePane : focusedPane ?? savedPane;

    public static bool ResolveSoloMode(WorkspaceSnapshot workspace)
        => ResolveDisplayMode(workspace) == DisplayMode.Solo;

    /// <summary>新しく開いた部屋の表示モード。<b>ドック</b>——初めて入る部屋で、面が1枚だけ
    /// 大きく出ている（集中）でも、名前の付いたタイルが組んである（分割）でもなく、
    /// <em>何が住んでいる部屋なのか</em>が一目で読めるのはドックだけ。中央に本文、下にシェル、
    /// 右に脇の面、そして帯に残り全部の取っ手が並ぶ姿が、そのまま部屋の見取り図になる。
    /// <para>これは<b>作られた瞬間の1回だけ</b>効く（<c>WorkspaceListViewModel.ActivateFolder</c>）。
    /// 以降はその部屋が最後に居たモードが正本で、保存済みの部屋には一切触らない。</para></summary>
    public const DisplayMode DefaultDisplayMode = DisplayMode.Dock;

    /// <summary>復元する表示モード。<c>Mode</c> の無い旧データは <c>Stage.IsActive</c> から移行する
    /// （ドックは後から足したモードなので、旧データがドックになることはない——
    /// <see cref="DefaultDisplayMode"/> をここへ持ち込まない理由でもある。既に保存のある部屋を
    /// 新しい既定で塗り替えると、前に閉じたときの姿で開き直せなくなる）。</summary>
    public static DisplayMode ResolveDisplayMode(WorkspaceSnapshot workspace) => workspace.Mode switch
    {
        DisplayMode.Solo => DisplayMode.Solo,
        DisplayMode.Layout => DisplayMode.Layout,
        DisplayMode.Dock => DisplayMode.Dock,
        _ => workspace.Stage?.IsActive == true ? DisplayMode.Solo : DisplayMode.Layout,
    };

    public static string NormalizeBrowserAddress(string? text, string defaultUrl)
    {
        var address = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(address))
            return defaultUrl;
        // スキーム付きの URL はそのまま通す。ただし「絶対 URI として解釈できるか」だけで判断しない——
        // URI のスキームはドットも数字も許すので、`localhost:5173` はスキーム "localhost" の
        // 絶対 URI として通ってしまい、そのままではどこへも遷移しない文字列が返っていた。
        if (KnownSchemes.Contains(SchemeOf(address))
            && Uri.TryCreate(address, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme))
            return uri.ToString();
        // ローカルパス（`C:\…` と UNC の `\\srv\share\…`）は file: URI へ。スキーム判定を既知の名前に
        // 絞った副作用で、`C:\notes\a.html` のドライブレターが未知スキーム "C" として素通りし、
        // `https://C:\notes\a.html` という Uri に載らない文字列を作ってしまう（＝遷移で例外）。
        if (TryLocalPathUri(address) is { } fileUri)
            return fileUri;
        // 空白を含むものはホスト名になり得ないので、ローカル判定より先に検索語として抜く
        // （「localhost is down」のような文が `http://localhost is down` になって遷移で弾かれていた）。
        if (address.Contains(' '))
            return SearchUrl(address);
        var isLocal = address.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                      || address.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                      // `devbox:3000` のような社内・開発サーバー（ドット無しホスト＋ポート）も
                      // ホストとして扱う。ドット判定だけだと検索へ流れて、どこへも行けなくなる。
                      || (!address.Contains('.') && HasPortSuffix(address));
        // ホスト名に見えないものは検索語として扱う。ドットの無い一語（「loomo」等）も——
        // https://loomo へ行っても名前が引けず、ただの失敗ページになる。
        if (!isLocal && !address.Contains('.'))
            return SearchUrl(address);
        // 名前が引ける保証のない社内ホストは http：この形（ドット無し＋ポート）で待っているのは
        // ほぼ開発サーバーで、https だと証明書以前に接続できない。
        return (isLocal ? "http://" : "https://") + address;
    }

    /// <summary>アドレスを検索語として扱うときの遷移先。</summary>
    private static string SearchUrl(string address)
        => $"https://www.google.com/search?q={Uri.EscapeDataString(address)}";

    /// <summary><c>ホスト:数字</c>（パス・クエリが続いてもよい）の形か。</summary>
    private static bool HasPortSuffix(string address)
    {
        var colon = address.IndexOf(':');
        if (colon <= 0)
            return false;
        var rest = address[(colon + 1)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var port = end < 0 ? rest : rest[..end];
        return port.Length is > 0 and <= 5 && port.All(char.IsAsciiDigit);
    }

    /// <summary>アドレス欄にそのまま渡してよいスキーム（それ以外の "xxx:" はホスト名か検索語として扱う）。</summary>
    /// <remarks><c>chrome-extension</c> は拡張機能の設定画面・ポップアップの URL（§21.5.2）。
    /// これが無いと、こちらから開く設定画面がまるごと検索語として Google へ流れる。</remarks>
    private static readonly HashSet<string> KnownSchemes = new(StringComparer.OrdinalIgnoreCase) {
        "http", "https", "file", "about", "data", "view-source", "ftp", "mailto", "edge", "chrome",
        "chrome-extension",
    };

    /// <summary>先頭の <c>スキーム:</c> 部分（無ければ空文字）。</summary>
    private static string SchemeOf(string address)
    {
        var colon = address.IndexOf(':');
        return colon > 0 ? address[..colon] : "";
    }

    /// <summary>ローカルパスなら <c>file:///…</c> を返す（そうでなければ null）。</summary>
    private static string? TryLocalPathUri(string address)
    {
        try
        {
            return Path.IsPathRooted(address) && !address.StartsWith('/')
                ? new Uri(address).AbsoluteUri
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException)
        {
            return null;   // パスに見えて Uri へ載らないものは、後段の検索語／ホスト名の判定に任せる
        }
    }

    internal static void RestoreEditor(VimEditorControl editor, EditorTabSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.FilePath) && File.Exists(snapshot.FilePath))
        {
            editor.LoadFile(snapshot.FilePath);
            if (!snapshot.IsModified)
            {
                RestoreEditorViewState(editor, snapshot);
                return;
            }
        }
        if (snapshot.IsModified || string.IsNullOrWhiteSpace(snapshot.FilePath))
        {
            editor.SetText(snapshot.LoadText());
            RestoreEditorViewState(editor, snapshot);
            return;
        }
        editor.SetText(string.Empty);
    }

    internal static EditorTabSnapshot CaptureEditorTab(EditorTab tab, Guid? activeTabId)
    {
        var isActive = tab.Id == activeTabId;
        if (!tab.IsRealized && tab.Pending is { } pending)
        {
            return new EditorTabSnapshot
            {
                Id = tab.Id,
                FilePath = pending.FilePath,
                Text = pending.Text,
                DeferredTextPath = pending.DeferredTextPath,
                Title = pending.Title,
                IsModified = pending.IsModified,
                IsActive = isActive,
                CaretLine = pending.CaretLine,
                CaretColumn = pending.CaretColumn,
                ScrollRatio = pending.ScrollRatio
            };
        }

        var editor = tab.Control;
        return new EditorTabSnapshot
        {
            Id = tab.Id,
            FilePath = editor.FilePath,
            // 本文は「下書きへ書くとき」＝未保存か名前無しのタブだけ読む。`VimEditorControl.Text` は
            // 行配列を毎回 join して 1 本の文字列を作る（456KB のファイルで 0.4ms・LOH 行き）ので、
            // 保存されているタブの本文まで読むと、打鍵のたびに開いている全タブぶんの複製が走っていた。
            // 復元側（RestoreEditor）もこの条件でしか本文を使わない。§31.15
            Text = editor.IsModified || string.IsNullOrWhiteSpace(editor.FilePath) ? editor.Text : null,
            Title = string.IsNullOrWhiteSpace(editor.FilePath) ? "Untitled" : Path.GetFileName(editor.FilePath),
            IsModified = editor.IsModified,
            IsActive = isActive,
            CaretLine = editor.Caret.Line,
            CaretColumn = editor.Caret.Column,
            ScrollRatio = editor.VerticalScrollRatio
        };
    }

    private static void RestoreEditorViewState(VimEditorControl editor, EditorTabSnapshot snapshot)
    {
        // 0 行・0 列、先頭スクロールも明示的な保存状態。現在値がたまたま初期値と同じことへ依存しない。
        editor.NavigateTo(Math.Max(0, snapshot.CaretLine), Math.Max(0, snapshot.CaretColumn));
        if (snapshot.ScrollRatio is { } ratio && double.IsFinite(ratio))
            editor.Dispatcher.BeginInvoke(
                new Action(() => editor.ScrollToVerticalRatio(Math.Clamp(ratio, 0, 1))), DispatcherPriority.Loaded);
    }
}

public sealed record WorkspaceTabRestorePlan<TSnapshot>(
    IReadOnlyList<TSnapshot> Snapshots, int ActiveIndex);
