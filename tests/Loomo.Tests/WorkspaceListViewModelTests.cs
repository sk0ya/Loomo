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

    [Fact]
    public void Selecting_workspace_entry_activates_workspace()
    {
        var dir1 = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var dir2 = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var store = new WorkspaceStateStore(Path.Combine(
            Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json"));
        var sut = new WorkspaceListViewModel(store);
        WorkspaceSnapshot? activated = null;
        sut.WorkspaceActivated += (_, snapshot) => activated = snapshot;

        sut.ActivateFolder(dir1.FullName);
        sut.ActivateFolder(dir2.FullName);
        var first = sut.Workspaces.Single(w => w.RootPath == dir1.FullName);

        sut.SelectedWorkspace = first;

        Assert.Equal(dir1.FullName, activated?.RootPath);
        Assert.Equal(first, sut.SelectedWorkspace);
    }

    [Fact]
    public void Workspace_state_round_trips_stage_snapshot()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json");
        var store = new WorkspaceStateStore(path);
        var workspace = new WorkspaceSnapshot
        {
            RootPath = "C:\\work",
            Stage = new StageSnapshot
            {
                IsActive = true,
                Pane = PaneKind.Diff
            }
        };

        store.Save(new WorkspaceState
        {
            ActiveWorkspaceId = workspace.Id,
            Workspaces = [workspace]
        });

        var loaded = store.Load().Workspaces.Single();
        Assert.True(loaded.Stage?.IsActive);
        Assert.Equal(PaneKind.Diff, loaded.Stage?.Pane);
    }
}
