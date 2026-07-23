using System.IO;
using System.Linq;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
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
    private static ShellViewModel CreateSut()
    {
        var workspace = new FakeWorkspaceService();
        var folderTree = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-test-workflows")),
            new FolderTreeCommandHandler(workspace), new FolderTreeQuery());

        var approval = new UiApprovalService();
        var settings = new AiSettings();
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
        var store = new AiSettingsStore(Path.Combine(Path.GetTempPath(), "loomo-test-settings.json"));
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
        var lspService = new sk0ya.Loomo.Services.Lsp.LspManagementService(new FakeTerminalService());
        var lspVm = new LspSettingsViewModel(lspService);
        var lspPromptVm = new LspPromptViewModel(lspService, settings, store);
        var formatterVm = new FormatterSettingsViewModel(
            new sk0ya.Loomo.Services.Formatting.FormatterManagementService(new FakeTerminalService()));
        var keyboardVm = new KeybindingsViewModel(new sk0ya.Loomo.App.Input.KeybindingService(settings, store));

        var workspaceStore = new WorkspaceStateStore(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workspaces.json"));
        var workspacesVm = new WorkspaceListViewModel(workspaceStore);

        var git = new sk0ya.Loomo.Services.GitService(workspace);
        var rootSwitch = new GitRootSwitchViewModel(git, workspace);
        var diffJournal = new sk0ya.Loomo.Core.Diff.FileChangeJournal();
        var diffFiles = new DiffFileGateway();
        var diffSessionVm = new DiffSessionViewModel(diffJournal, git, new FakeEditorService(), workspace,
            diffFiles, new DiffSessionQuery(diffJournal, git),
            new DiffSessionCommandHandler(diffFiles, diffJournal, git));
        var gitPanelVm = new GitPanelViewModel(git, new FakeEditorService(), workspace, diffSessionVm, rootSwitch);
        var gitQuery = new GitSessionQuery(git);
        var gitSessionVm = new GitSessionViewModel(git, new FakeEditorService(), diffSessionVm,
            gitQuery, new GitSessionCommandHandler(git), new GitHistoryViewModel(gitQuery), rootSwitch);
        var traceSessionVm = new TraceSessionViewModel(
            new TraceReader(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-traces")));

        var searchService = new sk0ya.Loomo.Services.Search.WorkspaceSearchService(workspace);
        var searchMapper = new SearchResultTreeMapper();
        var searchVm = new SearchPanelViewModel(workspace,
            new SearchPanelQuery(searchService, searchMapper), searchMapper);

        var debugVm = new DebugViewModel(
            new sk0ya.Loomo.Services.Debug.NetcoredbgDebugSessionFactory(), workspace, new FakeTerminalService(),
            new sk0ya.Loomo.Services.Debug.TestDiscoveryService(),
            new sk0ya.Loomo.Core.Debug.DebugLaunchProfileStore(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-launch-profiles.json")));

        return new ShellViewModel(folderTree, workspacesVm, aiBar, new TabsViewModel(), sessionsVm, settingsVm,
            appearanceVm, lspVm, lspPromptVm, formatterVm, keyboardVm, gitPanelVm, gitSessionVm, diffSessionVm, traceSessionVm,
            new PegboardViewModel(), searchVm, debugVm,
            new TrailViewModel(new TrailStore(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-trail.db"))));
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

    [Fact]
    public void ShowTabs_switches_panel_and_keeps_open()
    {
        var sut = CreateSut();

        sut.ShowTabsCommand.Execute(null);
        Assert.True(sut.IsSidebarVisible);
        Assert.Equal(SidebarPanel.Tabs, sut.ActivePanel);
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
}
