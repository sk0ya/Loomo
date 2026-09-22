using System.IO;
using System.Linq;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ActivityBar からのサイドバー切替/開閉ロジック（ShellViewModel）の検証。
/// UI（列幅・WindowChrome）は ViewModel の IsSidebarVisible / ActivePanel に追従する。
/// </summary>
public class ShellViewModelTests
{
    [Fact]
    public void 再起動で選択項目と閉じた区画を復元する()
    {
        var settings = new LoomoSettings();
        var first = CreateSut(activityBar: new ActivityBarViewModel(settings));
        first.ActivePanel = SidebarPanel.Pegboard;
        first.IsSecondarySidebarVisible = false;

        var restored = CreateSut(activityBar: new ActivityBarViewModel(settings));

        Assert.Equal(SidebarPanel.Pegboard, restored.ActivePanel);
        Assert.True(restored.IsSidebarVisible);
        Assert.False(restored.IsSecondarySidebarVisible);
        Assert.True(restored.ActivityBar.ItemFor(SidebarPanel.Pegboard)!.IsSelected);
    }

    [Fact]
    public void 保存した選択が配置と合わなければ同じ段の項目へ戻す()
    {
        var settings = new LoomoSettings();
        settings.ActivityBar.PrimarySelection = "tabs";
        settings.ActivityBar.SecondarySelection = "unknown";
        settings.ActivityBar.PrimaryVisible = false;

        var restored = CreateSut(activityBar: new ActivityBarViewModel(settings));

        Assert.Equal(SidebarPanel.Explorer, restored.ActivePanel);
        Assert.Equal(SidebarPanel.Tabs, restored.SecondaryPanel);
        Assert.False(restored.IsSidebarVisible);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ソリューションだけの区画は遅延検出後に保存した開閉状態へ戻る(bool primary, bool visible)
    {
        var settings = new LoomoSettings();
        settings.ActivityBar.Primary = primary ? ["solution"] : ["explorer", "git", "pegboard", "tabs"];
        settings.ActivityBar.Secondary = primary ? ["explorer", "git", "pegboard", "tabs"] : ["solution"];
        settings.ActivityBar.PrimarySelection = primary ? "solution" : "explorer";
        settings.ActivityBar.SecondarySelection = primary ? "tabs" : "solution";
        settings.ActivityBar.PrimaryVisible = primary ? visible : true;
        settings.ActivityBar.SecondaryVisible = primary ? true : visible;
        var service = new StubSolutionModelService(EmptySolution());
        using var solution = new CSharpSolutionExplorerViewModel(service);
        var sut = CreateSut(solution, new ActivityBarViewModel(settings));

        Assert.False(primary ? sut.IsSidebarVisible : sut.IsSecondarySidebarVisible);
        service.Publish(CSharpSolution());

        Assert.Equal(SidebarPanel.Solution, primary ? sut.ActivePanel : sut.SecondaryPanel);
        Assert.Equal(visible, primary ? sut.IsSidebarVisible : sut.IsSecondarySidebarVisible);
        Assert.Equal(visible, sut.ActivityBar.ItemFor(SidebarPanel.Solution)!.IsSelected);
        Assert.Equal(visible, primary ? settings.ActivityBar.PrimaryVisible : settings.ActivityBar.SecondaryVisible);
    }

    private static ShellViewModel CreateSut(CSharpSolutionExplorerViewModel? solutionExplorer = null,
        ActivityBarViewModel? activityBar = null)
    {
        var workspace = new FakeWorkspaceService();
        var folderTree = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-test-workflows")),
            new FolderTreeCommandHandler(workspace, new FileOperationHistory()), new FolderTreeQuery());

        var approval = new UiApprovalService();
        var settings = new LoomoSettings();
        var orchestrator = new AgentOrchestrator(
            new FakeAiClientFactory(),
            new ToolRegistry(Enumerable.Empty<IAgentTool>()),
            approval,
            new SafetyPolicy(new SafetySettings()),
            NoopContextWindowPolicy.Instance,
            NullLogger<AgentOrchestrator>.Instance);

        var conversations = new ConversationStore(
            Path.Combine(Path.GetTempPath(), "loomo-test-sessions"));

        // 保存先はテスト用の一時パス（コンストラクタでは I/O しない）
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "loomo-test-settings.json"));
        var modelCatalog = new ModelCatalogService(settings);
        var modelDownload = new ModelDownloadService(new System.Net.Http.HttpClient());
        var settingsEditor = new FakeEditorService();
        var modelFolders = new ModelFolderGateway();
        var settingsVm = new SettingsViewModel(settings, store, settingsEditor, modelCatalog, modelDownload,
            new FakeAiWarmup(), new ModelFolderPicker(modelFolders), new BlockedCommandsHandler(settings, store, settingsEditor),
            new SettingsPersistenceHandler(settings, store), new SettingsModelChoiceMapper());
        var workflowStore = new WorkflowStore(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workflows"));
        var workflowRunner = new WorkflowToolRunner(
            new FakeTerminalService(), new FakeWorkspaceService(), new FakeEditorService(),
            new SafetyPolicy(new SafetySettings()));
        var workflowVm = new WorkflowViewModel(orchestrator, approval, workflowStore, new FakeAiWarmup(), settings, workflowRunner, workspace);
        var aiBar = new AiBarViewModel(orchestrator, approval, settings, settingsVm, conversations,
            new PromptHistoryStore(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-history.json")),
            new FakeAiWarmup(), workflowVm);
        var sessionsVm = new SessionsViewModel(conversations, aiBar,
            new TraceReader(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-traces")));
        var appearanceVm = new AppearanceViewModel(settings, store, new ThemeManager(), new UiFontManager());
        var lspService = new sk0ya.Loomo.Services.Lsp.LspManagementService(new FakeTerminalService(), new sk0ya.Loomo.Services.Lsp.LspServerTable(null));
        var lspVm = new LspSettingsViewModel(lspService);
        var lspPromptVm = new LspPromptViewModel(lspService, settings, store);
        var formatterVm = new FormatterSettingsViewModel(
            new sk0ya.Loomo.Services.Formatting.FormatterManagementService(
                new FakeTerminalService(), new Editor.Core.Formatting.FormatterRegistry()));
        var keyboardVm = new KeybindingsViewModel(new sk0ya.Loomo.App.Input.KeybindingService(settings, store));

        var workspaceStore = new WorkspaceStateStore(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workspaces.json"));
        var workspacesVm = new WorkspaceListViewModel(workspaceStore);

        var git = new sk0ya.Loomo.Services.GitService(workspace);
        var rootSwitch = new GitRootSwitchViewModel(git, workspace);
        var diffFiles = new DiffFileGateway();
        // 実アプリと同じく Git パネルと Diff ペインで1つを共有する（Singleton 相当）。
        var compareBase = new GitCompareBaseViewModel(git);
        var diffSessionVm = new DiffSessionViewModel(git, new FakeEditorService(), workspace,
            diffFiles, new DiffSessionQuery(git), new DiffSessionCommandHandler(git), new LoomoSettings(),
            compareBase);
        var gitPanelVm = new GitPanelViewModel(
            git, new FakeEditorService(), workspace, rootSwitch, compareBase);
        var gitQuery = new GitSessionQuery(git);
        var gitSessionVm = new GitSessionViewModel(git, new FakeEditorService(),
            gitQuery, new GitSessionCommandHandler(git), new GitHistoryViewModel(gitQuery), rootSwitch);
        var traceSessionVm = new TraceSessionViewModel(
            new TraceReader(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-traces")));

        var searchService = new sk0ya.Loomo.Services.Search.WorkspaceSearchService(workspace);
        var searchMapper = new SearchResultTreeMapper();
        var searchVm = new SearchPanelViewModel(workspace,
            new SearchPanelQuery(searchService, searchMapper), searchMapper);

        var debugVm = new DebugViewModel(
            new sk0ya.Loomo.Services.Debug.NetcoredbgDebugSessionFactory(), workspace, new FakeTerminalService(),
            new sk0ya.Loomo.CSharp.Testing.TestDiscoveryService(),
            new sk0ya.Loomo.Core.Debug.DebugLaunchProfileStore(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-launch-profiles.json")));

        var tsIdeVm = new TsDebugViewModel(
            new sk0ya.Loomo.Services.Debug.Js.JsDebugSessionFactory(), workspace, new FakeTerminalService(),
            new FakeBrowserService(),
            new sk0ya.Loomo.Core.Debug.DebugLaunchProfileStore(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-ts-launch-profiles.json")));

        var filesVm = new FilesPaneViewModel(
            workspace, new FolderTreeCommandHandler(workspace, new FileOperationHistory()), folderTree, new FakeFilePlacesProvider());

        return new ShellViewModel(folderTree, filesVm, workspacesVm, aiBar, new TabsViewModel(), sessionsVm, settingsVm,
            appearanceVm, lspVm, lspPromptVm, formatterVm, keyboardVm, gitPanelVm, gitSessionVm, diffSessionVm, traceSessionVm,
            new PegboardViewModel(),
            new BrowserViewModel(new BrowserLibraryStore(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-browser.json"))),
            searchVm, debugVm, tsIdeVm,
            new TrailViewModel(new TrailStore(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-trail.db"))),
            csharpSolutionExplorer: solutionExplorer, activityBar: activityBar);
    }

    [Fact]
    public void Sidebar_shows_explorer_by_default()
    {
        var sut = CreateSut();
        Assert.True(sut.IsSidebarVisible);
        Assert.Equal(SidebarPanel.Explorer, sut.ActivePanel);
    }

    [Fact]
    public void ShowExplorer_on_active_panel_collapses_then_reopens()
    {
        var sut = CreateSut();

        sut.ShowExplorerCommand.Execute(null);   // 同一パネル再クリック → 閉じる
        Assert.False(sut.IsSidebarVisible);

        sut.ShowExplorerCommand.Execute(null);   // 再度クリック → 開く
        Assert.True(sut.IsSidebarVisible);
    }

    [Fact]
    public void ShowSettings_opens_overlay_on_appearance_category()
    {
        var sut = CreateSut();

        sut.ShowSettingsCommand.Execute(null);
        Assert.True(sut.IsSettingsOverlayOpen);
        Assert.Equal(SettingsCategory.Appearance, sut.SettingsCategory);
    }

    [Fact]
    public void ShowAppearance_opens_overlay_on_appearance_category()
    {
        var sut = CreateSut();

        sut.ShowAppearanceCommand.Execute(null);
        Assert.True(sut.IsSettingsOverlayOpen);
        Assert.Equal(SettingsCategory.Appearance, sut.SettingsCategory);
    }

    /// <summary>タブ一覧は独立した面で、既定では中段バーに住む。押しても上段の面（エクスプローラ）は
    /// 閉じない——2本のバーが別々の区画を持つのが要点。</summary>
    [Fact]
    public void ShowTabs_toggles_the_secondary_section_without_touching_the_primary()
    {
        var sut = CreateSut();
        Assert.Equal(ActivityBarSlot.Secondary, sut.ActivityBar.SlotOf(SidebarPanel.Tabs));

        sut.ShowTabsCommand.Execute(null);   // 既定で開いているので畳む
        Assert.False(sut.IsSecondarySidebarVisible);
        Assert.True(sut.IsSidebarVisible);
        Assert.Equal(SidebarPanel.Explorer, sut.ActivePanel);

        sut.ShowTabsCommand.Execute(null);   // もう一度で開く
        Assert.True(sut.IsSecondarySidebarVisible);
        Assert.Equal(SidebarPanel.Tabs, sut.SecondaryPanel);
        Assert.True(sut.IsSidebarVisible);
        Assert.Equal(SidebarPanel.Explorer, sut.ActivePanel);
    }

    /// <summary>2本のバーはそれぞれ自分の区画を持つ：上段の面と中段の面が同時に見えていること。</summary>
    [Fact]
    public void 上段と中段のパネルは同時に見える()
    {
        var sut = CreateSut();

        Assert.True(sut.IsSidebarVisible);
        Assert.Equal(SidebarPanel.Explorer, sut.ActivePanel);
        Assert.True(sut.IsSecondarySidebarVisible);
        Assert.Equal(SidebarPanel.Tabs, sut.SecondaryPanel);
        Assert.True(sut.IsSidebarColumnVisible);

        // 上段を畳んでも中段は残り、列そのものは立ったまま。
        sut.ShowExplorerCommand.Execute(null);
        Assert.False(sut.IsSidebarVisible);
        Assert.True(sut.IsSecondarySidebarVisible);
        Assert.True(sut.IsSidebarColumnVisible);

        // 両方畳めば列ごと消える。
        sut.ShowTabsCommand.Execute(null);
        Assert.False(sut.IsSidebarColumnVisible);
    }

    /// <summary>保存された配置で立ち上げたときも、区画が「その段に居ない面」を映さないこと。
    /// 既定の <c>ActivePanel</c>／<c>SecondaryPanel</c> と保存された段割りは食い違いうる。</summary>
    [Fact]
    public void 保存された配置で起動しても区画は自分の段の面を映す()
    {
        var settings = new LoomoSettings();
        // 前回：エクスプローラを中段へ、タブ一覧を上段へ動かしていた部屋。
        settings.ActivityBar.Primary = ["tabs", "git", "solution", "pegboard"];
        settings.ActivityBar.Secondary = ["explorer"];
        var sut = CreateSut(activityBar: new ActivityBarViewModel(settings));

        Assert.Equal(SidebarPanel.Tabs, sut.ActivePanel);
        Assert.Equal(SidebarPanel.Explorer, sut.SecondaryPanel);
        Assert.True(sut.IsSidebarVisible);
        Assert.True(sut.IsSecondarySidebarVisible);
    }

    /// <summary>全部を上段へ集めてあった部屋では、中身の無い中段は畳んだ状態で立ち上げること。
    /// 開いたままだと「上段を閉じても空の列が残り、閉じる導線が無い」状態になる。</summary>
    [Fact]
    public void 空の段は畳んだ状態で起動する()
    {
        var settings = new LoomoSettings();
        settings.ActivityBar.Primary = ["explorer", "git", "solution", "pegboard", "tabs"];
        settings.ActivityBar.Secondary = [];
        var sut = CreateSut(activityBar: new ActivityBarViewModel(settings));

        Assert.False(sut.IsSecondarySidebarVisible);
        Assert.True(sut.IsSidebarColumnVisible);

        sut.ShowExplorerCommand.Execute(null);   // 上段も閉じる
        Assert.False(sut.IsSidebarColumnVisible);
    }

    /// <summary>同じ段での並べ替えでは面を開き直さない（人間がしていないナビゲーション＝§27）。</summary>
    [Fact]
    public void 同じ段での並べ替えでは面を開き直さない()
    {
        var sut = CreateSut();
        sut.ShowExplorerCommand.Execute(null);   // 上段を畳む
        Assert.False(sut.IsSidebarVisible);

        sut.ActivityBar.Move(sut.ActivityBar.ItemFor(SidebarPanel.Git)!, ActivityBarSlot.Primary, 0);

        Assert.False(sut.IsSidebarVisible);      // 畳んだまま
    }

    /// <summary>ドラッグで段を移した項目は、移した先の区画で開いて見せる。取り残された区画は
    /// その段の先頭の面へ寄せ直し、段が空になったら畳む。</summary>
    [Fact]
    public void 段を移した項目は移した先で開き元の区画は寄せ直す()
    {
        var sut = CreateSut();

        sut.ActivityBar.Move(sut.ActivityBar.ItemFor(SidebarPanel.Explorer)!, ActivityBarSlot.Secondary, 0);

        Assert.Equal(SidebarPanel.Explorer, sut.SecondaryPanel);
        Assert.True(sut.IsSecondarySidebarVisible);
        // 上段はエクスプローラを失ったので先頭（Git）へ。
        Assert.Equal(SidebarPanel.Git, sut.ActivePanel);
        Assert.True(sut.IsSidebarVisible);

        // 以後、エクスプローラのアイコンは中段の区画を開閉する。
        sut.ShowExplorerCommand.Execute(null);
        Assert.False(sut.IsSecondarySidebarVisible);
        Assert.Equal(SidebarPanel.Git, sut.ActivePanel);
    }

    [Fact]
    public void 段が空になったらその区画は畳む()
    {
        var sut = CreateSut();

        sut.ActivityBar.Move(sut.ActivityBar.ItemFor(SidebarPanel.Tabs)!, ActivityBarSlot.Primary, 0);

        Assert.Equal(SidebarPanel.Tabs, sut.ActivePanel);
        Assert.True(sut.IsSidebarVisible);
        Assert.False(sut.IsSecondarySidebarVisible);   // 中段は空になった
    }

    [Fact]
    public void ShowPegboard_switches_panel_and_keeps_open()
    {
        var sut = CreateSut();

        sut.ShowPegboardCommand.Execute(null);
        Assert.True(sut.IsSidebarVisible);
        Assert.Equal(SidebarPanel.Pegboard, sut.ActivePanel);
    }

    [Fact]
    public void ShowSettings_twice_closes_overlay()
    {
        var sut = CreateSut();

        sut.ShowSettingsCommand.Execute(null);   // 設定オーバーレイを開く
        sut.ShowSettingsCommand.Execute(null);   // 同一カテゴリ再クリック → 閉じる

        Assert.False(sut.IsSettingsOverlayOpen);
    }

    // ===== ソリューション（C#）パネル：C# のある部屋にだけ現れる、フォルダーツリーとは別の面 =====

    [Fact]
    public void CSharpのないワークスペースではソリューションパネルを出さない()
    {
        using var solution = new CSharpSolutionExplorerViewModel(
            new StubSolutionModelService(EmptySolution()));
        var sut = CreateSut(solution);

        Assert.False(sut.IsCSharpSolutionAvailable);
        sut.ShowSolutionCommand.Execute(null);
        Assert.Equal(SidebarPanel.Explorer, sut.ActivePanel);
    }

    [Fact]
    public void CSharpプロジェクトがあればソリューションパネルを開き再クリックで閉じる()
    {
        var service = new StubSolutionModelService(EmptySolution());
        using var solution = new CSharpSolutionExplorerViewModel(service);
        var sut = CreateSut(solution);
        var availabilityChanged = 0;
        sut.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(ShellViewModel.IsCSharpSolutionAvailable)) availabilityChanged++;
        };

        service.Publish(CSharpSolution());
        Assert.True(sut.IsCSharpSolutionAvailable);
        Assert.Equal(1, availabilityChanged);

        sut.ShowSolutionCommand.Execute(null);
        Assert.Equal(SidebarPanel.Solution, sut.ActivePanel);
        Assert.True(sut.IsSidebarVisible);

        sut.ShowSolutionCommand.Execute(null);
        Assert.False(sut.IsSidebarVisible);
    }

    /// <summary>C# の無いワークスペースへ切り替えたら、空のパネルを見せたままにしない。</summary>
    [Fact]
    public void ソリューションパネル表示中にCSharpが消えたらエクスプローラへ戻る()
    {
        var service = new StubSolutionModelService(CSharpSolution());
        using var solution = new CSharpSolutionExplorerViewModel(service);
        var sut = CreateSut(solution);
        sut.ShowSolutionCommand.Execute(null);
        Assert.Equal(SidebarPanel.Solution, sut.ActivePanel);

        service.Publish(EmptySolution());

        Assert.False(sut.IsCSharpSolutionAvailable);
        Assert.Equal(SidebarPanel.Explorer, sut.ActivePanel);
        Assert.True(sut.IsSidebarVisible);
    }

    /// <summary>軌跡から過去のパネルへ戻る経路も同じ判定を通ること。C# の消えた部屋で
    /// ソリューションを開くと、閉じる導線（ActivityBar のアイコン）の無い空の列が残る。</summary>
    [Fact]
    public void 軌跡から戻るときも出せないソリューションパネルは開かない()
    {
        var service = new StubSolutionModelService(CSharpSolution());
        using var solution = new CSharpSolutionExplorerViewModel(service);
        var sut = CreateSut(solution);

        Assert.True(sut.RestorePanel(SidebarPanel.Solution));
        Assert.Equal(SidebarPanel.Solution, sut.ActivePanel);

        service.Publish(EmptySolution());
        Assert.Equal(SidebarPanel.Explorer, sut.ActivePanel);

        Assert.False(sut.RestorePanel(SidebarPanel.Solution));
        Assert.Equal(SidebarPanel.Explorer, sut.ActivePanel);

        // C# のあるパネル以外は今までどおり戻れる。
        Assert.True(sut.RestorePanel(SidebarPanel.Git));
        Assert.Equal(SidebarPanel.Git, sut.ActivePanel);
        Assert.True(sut.IsSidebarVisible);
    }

    /// <summary>自動退避は人間のナビゲーションではないので、軌跡へ書かせない印を立てること（§27）。</summary>
    [Fact]
    public void CSharpが消えた自動退避は人間の操作として記録させない()
    {
        var service = new StubSolutionModelService(CSharpSolution());
        using var solution = new CSharpSolutionExplorerViewModel(service);
        var sut = CreateSut(solution);
        sut.ShowSolutionCommand.Execute(null);

        var automaticWhileChanging = (bool?)null;
        sut.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(ShellViewModel.ActivePanel))
                automaticWhileChanging = sut.IsPanelChangeAutomatic;
        };

        service.Publish(EmptySolution());

        Assert.Equal(SidebarPanel.Explorer, sut.ActivePanel);
        Assert.True(automaticWhileChanging);
        Assert.False(sut.IsPanelChangeAutomatic);   // 通知が終われば元へ戻る

        // 人間の操作（ActivityBar／コマンド）では立てない。
        sut.ShowGitCommand.Execute(null);
        Assert.Equal(SidebarPanel.Git, sut.ActivePanel);
        Assert.False(automaticWhileChanging);
    }

    private static SolutionModel EmptySolution()
        => new(null, "work", @"C:\work", [], ProjectLoadState.NotConfigured);

    private static SolutionModel CSharpSolution()
    {
        var project = new ProjectModel("App", @"C:\work\App\App.csproj", @"C:\work\App", [], [
            new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("Program.cs", @"C:\work\App\Program.cs")], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        return new SolutionModel(@"C:\work\App\App.sln", "App", @"C:\work\App",
            [project], ProjectLoadState.Ready);
    }

    /// <summary>ワークスペース切替を模して solution モデルを差し替えられるフェイク。</summary>
    private sealed class StubSolutionModelService(SolutionModel initial) : ISolutionModelService
    {
        public SolutionModel Current { get; private set; } = initial;
        public event EventHandler<SolutionModel>? Changed;

        public void Publish(SolutionModel model)
        {
            Current = model;
            Changed?.Invoke(this, model);
        }

        public Task<SolutionModel> ReloadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public ProjectModel? ProjectForFile(string filePath) => Current.ProjectForFile(filePath);
        public ProjectLoadState FileState(string filePath) => Current.ResolveFileState(filePath);
    }
}
