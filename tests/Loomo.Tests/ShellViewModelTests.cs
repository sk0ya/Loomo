using System.IO;
using System.Linq;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;
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
        var folderTree = new FolderTreeViewModel(workspace);

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
        var aiBar = new AiBarViewModel(orchestrator, approval, settings, conversations);
        var sessionsVm = new SessionsViewModel(conversations, aiBar);

        // 保存先はテスト用の一時パス（コンストラクタでは I/O しない）
        var store = new AiSettingsStore(Path.Combine(Path.GetTempPath(), "loomo-test-settings.json"));
        var copilotAuth = new CopilotAuthService(new System.Net.Http.HttpClient());
        var settingsVm = new SettingsViewModel(settings, store, copilotAuth, new FakeEditorService());
        var appearanceVm = new AppearanceViewModel(settings, store, new ThemeManager());

        var workspaceStore = new WorkspaceStateStore(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-loomo-workspaces.json"));
        var workspacesVm = new WorkspaceListViewModel(workspaceStore);

        return new ShellViewModel(folderTree, workspacesVm, aiBar, new TabsViewModel(), sessionsVm, settingsVm, appearanceVm);
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
    public void ShowSettings_switches_panel_and_keeps_open()
    {
        var sut = CreateSut();

        sut.ShowSettingsCommand.Execute(null);
        Assert.True(sut.IsSidebarVisible);
        Assert.Equal(SidebarPanel.Settings, sut.ActivePanel);
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
    public void ShowSettings_twice_collapses_sidebar()
    {
        var sut = CreateSut();

        sut.ShowSettingsCommand.Execute(null);   // Explorer → Settings（表示）
        sut.ShowSettingsCommand.Execute(null);   // 同一パネル再クリック → 閉じる

        Assert.False(sut.IsSidebarVisible);
        Assert.Equal(SidebarPanel.Settings, sut.ActivePanel);
    }
}
