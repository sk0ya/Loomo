using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>サイドバーに表示するパネル種別。ActivityBar のアイコン1つにつき1面で、
/// どの段（上段／中段）のバーに置くかは人間がドラッグで決める（<see cref="ActivityBarViewModel"/>）。</summary>
public enum SidebarPanel
{
    Explorer,
    Settings,
    Appearance,
    Git,
    Pegboard,
    /// <summary>開いているタブの一覧。エクスプローラ内のセクションから独立した面へ戻し、
    /// 既定では中段バー（2本目の ActivityBar）に住む。</summary>
    Tabs,
    /// <summary>C# ソリューションツリー。C# プロジェクトのあるワークスペースでだけ現れる
    /// （ActivityBar のアイコンごと出入りする）。フォルダーツリーとは別の面。</summary>
    Solution
}

/// <summary>中央オーバーレイ設定画面のカテゴリ（左ナビ）。</summary>
public enum SettingsCategory
{
    Appearance,
    Editor,
    Terminal,
    Ai,
    Lsp,
    Formatter,
    StyleCop,
    Keyboard
}

/// <summary>ルートウィンドウの ViewModel。各ペインの VM を束ねる。</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public FolderTreeViewModel FolderTree { get; }
    /// <summary>ファイル一覧ペイン（<see cref="Services.PaneKind.Files"/>）。サイドバーのツリーと
    /// 同居する別の面——階層の把握はツリー、集合の処理はこちら（§26.10）。</summary>
    public FilesPaneViewModel Files { get; }
    public WorkspaceListViewModel Workspaces { get; }
    public AiBarViewModel AiBar { get; }
    public TabsViewModel Tabs { get; }
    public SessionsViewModel Sessions { get; }
    public RecentItemsViewModel Recent { get; }
    public SettingsViewModel Settings { get; }
    public AppearanceViewModel Appearance { get; }
    public LspSettingsViewModel Lsp { get; }
    public LspPromptViewModel LspPrompt { get; }
    public FormatterSettingsViewModel Formatter { get; }
    public StyleCopSettingsViewModel StyleCop { get; }
    public KeybindingsViewModel Keyboard { get; }
    public GitPanelViewModel GitPanel { get; }
    public GitSessionViewModel GitSession { get; }
    public DiffSessionViewModel DiffSession { get; }
    public TraceSessionViewModel TraceSession { get; }
    public PegboardViewModel Pegboard { get; }
    /// <summary>評価済みsolution／projectの構造表示。通常のFolderTreeとは別のC#ビューで、
    /// サイドバーの独立したパネル（<see cref="SidebarPanel.Solution"/>）として住む。</summary>
    public CSharpSolutionExplorerViewModel? CSharpSolutionExplorer { get; }

    /// <summary>ソリューションパネルを出せるか（＝C#プロジェクトのあるワークスペースか）。
    /// ActivityBar のアイコンの出入りに使う。C# の無い部屋にC#の道具は置かない。</summary>
    public bool IsCSharpSolutionAvailable => CSharpSolutionExplorer?.IsVisible == true;
    /// <summary>ブラウザペインのツールバー状態・ブックマーク・履歴・ダウンロード（設計書 §21）。</summary>
    public BrowserViewModel Browser { get; }
    public SearchPanelViewModel SearchPanel { get; }
    public DebugViewModel Debug { get; }
    /// <summary>TS IDE（TypeScript / Node.js デバッグ）ペインのファサード。</summary>
    public TsDebugViewModel TsIde { get; }
    /// <summary>ウィンドウ最下部の軌跡（操作ログ）バー。クリックで通過した地点へ戻る。</summary>
    public TrailViewModel Trail { get; }
    /// <summary>右下に積み上げて表示する非モーダルなトースト通知（<see cref="Services.ToastService"/>）。</summary>
    public ToastHostViewModel Toasts { get; } = new();

    /// <summary>ActivityBar（左端の縦帯）2本ぶんの項目配置。どのアイコンがどちらの段に住むかを持ち、
    /// ドラッグ＆ドロップの結果を settings.json へ持ち越す。</summary>
    public ActivityBarViewModel ActivityBar { get; }

    /// <summary>上段サイドバー区画の表示状態。上段 ActivityBar のクリックで開閉する。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarColumnVisible))]
    private bool _isSidebarVisible = true;

    /// <summary>上段サイドバー区画に現在表示しているパネル。</summary>
    [ObservableProperty]
    private SidebarPanel _activePanel = SidebarPanel.Explorer;

    /// <summary>中段サイドバー区画の表示状態。中段 ActivityBar のクリックで開閉する。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarColumnVisible))]
    private bool _isSecondarySidebarVisible = true;

    /// <summary>中段サイドバー区画に現在表示しているパネル。既定はタブ一覧。</summary>
    [ObservableProperty]
    private SidebarPanel _secondaryPanel = SidebarPanel.Tabs;

    /// <summary>サイドバーの列そのものを出すか。2つの区画のどちらかが見えていれば出す。</summary>
    public bool IsSidebarColumnVisible => IsSidebarVisible || IsSecondarySidebarVisible;

    /// <summary>設定オーバーレイをキーボードカテゴリで開いているか（⌨ アイコンの強調用）。</summary>
    public bool IsKeyboardSettingsSelected =>
        IsSettingsOverlayOpen && SettingsCategory == SettingsCategory.Keyboard;

    /// <summary>設定オーバーレイを開いているか（⚙ アイコンの強調用）。キーボードは専用アイコンが
    /// あるので、そのカテゴリのときは歯車を強調しない——強調は常に1つだけにする。</summary>
    public bool IsSettingsSelected =>
        IsSettingsOverlayOpen && SettingsCategory != SettingsCategory.Keyboard;

    /// <summary>いま起きている <see cref="ActivePanel"/> の変更が自動退避（人間の操作ではない）か。
    /// 軌跡は「人間のナビゲーション」だけを記録する面（§27）なので、ホストはこれを見て記録と
    /// フォーカス移動を飛ばす。</summary>
    public bool IsPanelChangeAutomatic { get; private set; }

    /// <summary>中央オーバーレイの設定画面を開いているか。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsKeyboardSettingsSelected))]
    private bool _isSettingsOverlayOpen;

    /// <summary>設定オーバーレイで選択中のカテゴリ（左ナビ）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsSelected))]
    [NotifyPropertyChangedFor(nameof(IsKeyboardSettingsSelected))]
    private SettingsCategory _settingsCategory = SettingsCategory.Ai;

    public ShellViewModel(
        FolderTreeViewModel folderTree,
        FilesPaneViewModel files,
        WorkspaceListViewModel workspaces,
        AiBarViewModel aiBar,
        TabsViewModel tabs,
        SessionsViewModel sessions,
        SettingsViewModel settings,
        AppearanceViewModel appearance,
        LspSettingsViewModel lsp,
        LspPromptViewModel lspPrompt,
        FormatterSettingsViewModel formatter,
        KeybindingsViewModel keyboard,
        GitPanelViewModel gitPanel,
        GitSessionViewModel gitSession,
        DiffSessionViewModel diffSession,
        TraceSessionViewModel traceSession,
        PegboardViewModel pegboard,
        BrowserViewModel browser,
        SearchPanelViewModel searchPanel,
        DebugViewModel debug,
        TsDebugViewModel tsIde,
        TrailViewModel trail,
        RecentItemsViewModel? recent = null,
        CSharpSolutionExplorerViewModel? csharpSolutionExplorer = null,
        StyleCopSettingsViewModel? styleCop = null,
        ActivityBarViewModel? activityBar = null)
    {
        FolderTree = folderTree;
        Files = files;
        Workspaces = workspaces;
        AiBar = aiBar;
        Tabs = tabs;
        Sessions = sessions;
        Recent = recent ?? new RecentItemsViewModel(new RecentUsageService());
        Settings = settings;
        Appearance = appearance;
        Lsp = lsp;
        LspPrompt = lspPrompt;
        // 促しバーの「設定を開く」→ LSP 設定オーバーレイを開く。
        LspPrompt.OpenSettingsRequested += () => OpenSettingsOverlay(SettingsCategory.Lsp);
        Formatter = formatter;
        StyleCop = styleCop ?? new StyleCopSettingsViewModel();
        Keyboard = keyboard;
        GitPanel = gitPanel;
        GitSession = gitSession;
        DiffSession = diffSession;
        TraceSession = traceSession;
        Pegboard = pegboard;
        Browser = browser;
        SearchPanel = searchPanel;
        Debug = debug;
        TsIde = tsIde;
        Trail = trail;
        CSharpSolutionExplorer = csharpSolutionExplorer;
        ActivityBar = activityBar ?? new ActivityBarViewModel();
        ActivityBar.SetAvailable(SidebarPanel.Solution, IsCSharpSolutionAvailable);
        // 段を移した項目は移した先で開いて見せる（同じ段での並べ替えでは開き直さない——
        // 人間がしていないナビゲーションになる）。取り残された区画は既定のパネルへ寄せ直す。
        ActivityBar.ItemMoved += (_, moved) => {
            if (moved.SlotChanged) Reveal(moved.Item.Panel);
            NormalizeSections();
        };
        // 保存された配置は既定（上段＝エクスプローラ／中段＝タブ一覧）と食い違いうる。
        // 突き合わせずに立ち上げると、前回エクスプローラを中段へ動かしていた部屋では
        // 上段が空のまま開き、全部を上段へ集めていた部屋では「中身が無いのに畳めない列」が残る。
        var saved = ActivityBar.SavedState;
        _activePanel = ActivityBar.Items.FirstOrDefault(i => i.Id == saved.PrimarySelection)?.Panel ?? SidebarPanel.Explorer;
        _secondaryPanel = ActivityBar.Items.FirstOrDefault(i => i.Id == saved.SecondarySelection)?.Panel ?? SidebarPanel.Tabs;
        _isSidebarVisible = saved.PrimaryVisible;
        _isSecondarySidebarVisible = saved.SecondaryVisible;
        NormalizeSections();
        RefreshActivitySelection();
        UpdateGitPanelLive();
        _sectionPersistenceReady = true;
        if (CSharpSolutionExplorer is { } solutionExplorer)
            solutionExplorer.PropertyChanged += (_, e) => {
                if (e.PropertyName != nameof(CSharpSolutionExplorerViewModel.IsVisible)) return;
                OnPropertyChanged(nameof(IsCSharpSolutionAvailable));
                ActivityBar.SetAvailable(SidebarPanel.Solution, IsCSharpSolutionAvailable);
                // ワークスペース切替で C# が消えたら、空のパネルを見せたままにしない。
                if (IsCSharpSolutionAvailable)
                {
                    // 起動時のソリューション検出は遅れて完了するため、選択と開閉の復元をここで補う。
                    var restorePrimary = saved.PrimarySelection == "solution"
                        && ActivityBar.Holds(ActivityBarSlot.Primary, SidebarPanel.Solution);
                    var restoreSecondary = saved.SecondarySelection == "solution"
                        && ActivityBar.Holds(ActivityBarSlot.Secondary, SidebarPanel.Solution);
                    // パネル変更で設定も保存されるため、表示状態は変更前に退避する。
                    var primaryVisible = saved.PrimaryVisible;
                    var secondaryVisible = saved.SecondaryVisible;
                    if (restorePrimary)
                    {
                        ActivePanel = SidebarPanel.Solution;
                        IsSidebarVisible = primaryVisible;
                    }
                    if (restoreSecondary)
                    {
                        SecondaryPanel = SidebarPanel.Solution;
                        IsSecondarySidebarVisible = secondaryVisible;
                    }
                    return;
                }
                NormalizeSections();
            };

        // 設定保存時に AIバーのプロバイダ表示を更新する。
        Settings.Saved += AiBar.RefreshProviderLabel;
    }

    /// <summary>ActivityBar のエクスプローラアイコン。</summary>
    [RelayCommand]
    private void ShowExplorer() => Activate(SidebarPanel.Explorer);

    /// <summary>ActivityBar のタブ一覧アイコン。タブ一覧は独立した面で、既定では中段バーに住む。</summary>
    [RelayCommand]
    private void ShowTabs() => Activate(SidebarPanel.Tabs);

    /// <summary>ActivityBar のアイコンのクリック（どちらの段でも同じ経路）。</summary>
    [RelayCommand]
    private void ActivateActivityItem(ActivityBarItemViewModel? item)
    {
        if (item is not { IsAvailable: true }) return;
        Activate(item.Panel);
    }

    /// <summary>ActivityBar の設定（歯車）アイコン。中央オーバーレイの設定画面を外観カテゴリで開く
    /// （同じカテゴリで開いていれば閉じる＝トグル）。開くときにローカルのモデル一覧を取得する。</summary>
    [RelayCommand]
    private void ShowSettings() => OpenSettingsOverlay(SettingsCategory.Appearance);

    /// <summary>ActivityBar の外観（テーマ）アイコン。設定オーバーレイを外観カテゴリで開く。</summary>
    [RelayCommand]
    private void ShowAppearance() => OpenSettingsOverlay(SettingsCategory.Appearance);

    /// <summary>ActivityBar のエディタアイコン。設定オーバーレイをエディタカテゴリで開く。</summary>
    [RelayCommand]
    private void ShowEditorSettings() => OpenSettingsOverlay(SettingsCategory.Editor);

    /// <summary>ActivityBar のターミナルアイコン。設定オーバーレイをターミナルカテゴリで開く。</summary>
    [RelayCommand]
    private void ShowTerminalSettings() => OpenSettingsOverlay(SettingsCategory.Terminal);

    /// <summary>設定オーバーレイをキーボードカテゴリで開く。</summary>
    [RelayCommand]
    private void ShowKeyboardSettings() => OpenSettingsOverlay(SettingsCategory.Keyboard);

    /// <summary>設定オーバーレイを指定カテゴリで開く。既に同じカテゴリで開いていればトグルで閉じる。</summary>
    private void OpenSettingsOverlay(SettingsCategory category)
    {
        if (IsSettingsOverlayOpen && SettingsCategory == category)
        {
            IsSettingsOverlayOpen = false;
            return;
        }
        SettingsCategory = category;
        IsSettingsOverlayOpen = true;
        Settings.EnsureModelsLoaded();
    }

    /// <summary>
    /// 設定カテゴリが切り替わったときの追従処理。「言語サーバー」へはナビ項目の双方向バインドで
    /// 直接遷移する経路もある（<see cref="OpenSettingsOverlay"/> を通らない）ため、カテゴリ変化を
    /// 一元的に捕まえて一覧を取り直す。これをしないと一覧（導入状況）が空のまま「追加」フォームだけが見える。
    /// </summary>
    partial void OnSettingsCategoryChanged(SettingsCategory value)
    {
        if (value == SettingsCategory.Lsp)
            Lsp.Refresh();
        else if (value == SettingsCategory.Formatter)
            Formatter.Refresh();
        else if (value == SettingsCategory.StyleCop)
            StyleCop.Refresh();
    }

    /// <summary>設定オーバーレイを閉じる（Esc・背景クリック・閉じるボタン）。</summary>
    [RelayCommand]
    private void CloseSettingsOverlay() => IsSettingsOverlayOpen = false;

    /// <summary>ActivityBar のソリューションアイコン。C#プロジェクトのあるワークスペースでだけ押せる。</summary>
    [RelayCommand]
    private void ShowSolution()
    {
        if (!IsCSharpSolutionAvailable) return;
        Activate(SidebarPanel.Solution);
    }

    /// <summary>ActivityBar のペグボードアイコン（§23.3）。</summary>
    [RelayCommand]
    private void ShowPegboard() => Activate(SidebarPanel.Pegboard);

    /// <summary>軌跡（§27）から過去のパネルへ戻る。もう出せないパネル——C# の無い部屋の
    /// ソリューション——は開かない。ActivityBar のアイコンごと消えているので、開いてしまうと
    /// 中身が畳まれた空の列が残り、閉じる導線が無くなる。</summary>
    public bool RestorePanel(SidebarPanel panel)
    {
        if (panel == SidebarPanel.Solution && !IsCSharpSolutionAvailable) return false;
        Reveal(panel);
        return true;
    }

    /// <summary>エクスプローラを開く（トグルせず必ず開く）。エディタの現在ファイルをツリーで
    /// 選択・表示する「同期」機能用。</summary>
    public void RevealExplorerPanel() => Reveal(SidebarPanel.Explorer);

    /// <summary>そのパネルを、住んでいる段の区画で必ず開く（トグルしない）。</summary>
    private void Reveal(SidebarPanel panel)
    {
        if (ActivityBar.SlotOf(panel) == ActivityBarSlot.Secondary)
        {
            SecondaryPanel = panel;
            IsSecondarySidebarVisible = true;
            return;
        }
        ActivePanel = panel;
        IsSidebarVisible = true;
    }

    /// <summary>ActivityBar の Git アイコン。表示中は作業ツリーをライブ監視して自動更新する
    /// （実際の開始・停止は <see cref="UpdateGitPanelLive"/> が可視状態の変化に応じて行う）。</summary>
    [RelayCommand]
    private void ShowGit() => Activate(SidebarPanel.Git);

    // サイドバーの表示状態・選択パネルが変わるたびに、Git パネルのライブ監視と
    // ActivityBar のアイコン強調を入れ直す。
    partial void OnActivePanelChanged(SidebarPanel value) => OnSectionStateChanged();
    partial void OnIsSidebarVisibleChanged(bool value) => OnSectionStateChanged();
    partial void OnSecondaryPanelChanged(SidebarPanel value) => OnSectionStateChanged();
    partial void OnIsSecondarySidebarVisibleChanged(bool value) => OnSectionStateChanged();

    private bool _sectionPersistenceReady;

    private void OnSectionStateChanged()
    {
        UpdateGitPanelLive();
        RefreshActivitySelection();
        if (_sectionPersistenceReady)
        {
            var saved = ActivityBar.SavedState;
            saved.PrimarySelection = ActivityBar.ItemFor(ActivePanel)?.Id ?? "explorer";
            saved.SecondarySelection = ActivityBar.ItemFor(SecondaryPanel)?.Id ?? "tabs";
            saved.PrimaryVisible = IsSidebarVisible;
            saved.SecondaryVisible = IsSecondarySidebarVisible;
            ActivityBar.Persist();
        }
    }

    /// <summary>Git パネルが「見えている」ときだけライブ監視する。開いた瞬間に最新化される。
    /// どちらの段の区画に出していても「見えている」。</summary>
    private void UpdateGitPanelLive()
    {
        if (IsPanelShowing(SidebarPanel.Git))
            GitPanel.StartLiveTracking();
        else
            GitPanel.StopLiveTracking();
    }

    /// <summary>そのパネルがいまどちらかの区画に見えているか。</summary>
    public bool IsPanelShowing(SidebarPanel panel)
        => (IsSidebarVisible && ActivePanel == panel)
           || (IsSecondarySidebarVisible && SecondaryPanel == panel);

    /// <summary>ActivityBar のアイコン強調を今の区画の状態へ合わせる。強調は
    /// 「その段の区画が開いていて、かつそのパネル」のときだけ。</summary>
    private void RefreshActivitySelection()
    {
        foreach (var item in ActivityBar.Items)
            item.IsSelected = item.Slot == ActivityBarSlot.Secondary
                ? IsSecondarySidebarVisible && SecondaryPanel == item.Panel
                : IsSidebarVisible && ActivePanel == item.Panel;
    }

    /// <summary>区画が「もうその段に居ない／出せないパネル」を映したままにならないよう寄せ直す。
    /// 段が空になったらその区画ごと畳む。人間のナビゲーションではないので軌跡へは書かせない（§27）。</summary>
    private void NormalizeSections()
    {
        IsPanelChangeAutomatic = true;
        try
        {
            if (!ActivityBar.Holds(ActivityBarSlot.Primary, ActivePanel))
            {
                if (ActivityBar.FirstAvailablePanel(ActivityBarSlot.Primary) is { } fallback)
                    ActivePanel = fallback;
                else
                    IsSidebarVisible = false;
            }
            if (!ActivityBar.Holds(ActivityBarSlot.Secondary, SecondaryPanel))
            {
                if (ActivityBar.FirstAvailablePanel(ActivityBarSlot.Secondary) is { } fallback)
                    SecondaryPanel = fallback;
                else
                    IsSecondarySidebarVisible = false;
            }
        }
        finally { IsPanelChangeAutomatic = false; }
    }

    /// <summary>同じパネルを再クリックしたら閉じ、別パネルなら切替えて開く（VS Code 風）。
    /// 効く区画は、そのアイコンが住んでいる段のもの。</summary>
    private void Activate(SidebarPanel panel)
    {
        if (ActivityBar.SlotOf(panel) == ActivityBarSlot.Secondary)
        {
            if (IsSecondarySidebarVisible && SecondaryPanel == panel)
                IsSecondarySidebarVisible = false;
            else
                Reveal(panel);
            return;
        }
        if (IsSidebarVisible && ActivePanel == panel)
            IsSidebarVisible = false;
        else
            Reveal(panel);
    }
}
