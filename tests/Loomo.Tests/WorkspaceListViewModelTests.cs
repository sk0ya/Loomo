using System;
using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

public class WorkspaceListViewModelTests
{
    [Fact]
    public void Activating_current_workspace_does_not_raise_activation_event()
    {
        var dir = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var store = new WorkspaceStateStore(Path.Combine(
            Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json"));
        var sut = new WorkspaceListViewModel(store);
        var activationCount = 0;
        sut.WorkspaceActivated += (_, _) => activationCount++;

        sut.ActivateFolder(dir.FullName);
        sut.ActivateWorkspaceCommand.Execute(sut.Workspaces[0]);

        Assert.Equal(1, activationCount);
    }
}
