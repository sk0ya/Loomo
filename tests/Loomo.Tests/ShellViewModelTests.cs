using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ActivityBar からのサイドバー開閉ロジック（ShellViewModel）の検証。
/// UI（列幅・WindowChrome）は ViewModel の IsSidebarVisible に追従する。
/// </summary>
public class ShellViewModelTests
{
    private static ShellViewModel CreateSut()
    {
        var workspace = new FakeWorkspaceService();
        var folderTree = new FolderTreeViewModel(workspace);

        var approval = new UiApprovalService();
        var orchestrator = new AgentOrchestrator(
            new FakeAiClientFactory(),
            new ToolRegistry(Enumerable.Empty<IAgentTool>()),
            approval,
            NullLogger<AgentOrchestrator>.Instance);
        var aiBar = new AiBarViewModel(orchestrator, approval);

        return new ShellViewModel(folderTree, aiBar);
    }

    [Fact]
    public void Sidebar_is_visible_by_default()
    {
        var sut = CreateSut();
        Assert.True(sut.IsSidebarVisible);
    }

    [Fact]
    public void ToggleSidebar_flips_visibility()
    {
        var sut = CreateSut();

        sut.ToggleSidebarCommand.Execute(null);
        Assert.False(sut.IsSidebarVisible);

        sut.ToggleSidebarCommand.Execute(null);
        Assert.True(sut.IsSidebarVisible);
    }

    [Fact]
    public void ToggleSidebar_raises_property_changed()
    {
        var sut = CreateSut();
        var raised = 0;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.IsSidebarVisible)) raised++;
        };

        sut.ToggleSidebarCommand.Execute(null);

        Assert.Equal(1, raised);
    }
}
