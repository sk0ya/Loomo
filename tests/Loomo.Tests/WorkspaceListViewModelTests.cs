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
    public void Removing_active_workspace_switches_to_another_and_drops_it()
    {
        var dir1 = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var dir2 = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var storePath = Path.Combine(
            Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json");
        var store = new WorkspaceStateStore(storePath);
        var sut = new WorkspaceListViewModel(store);

        sut.ActivateFolder(dir1.FullName);
        sut.ActivateFolder(dir2.FullName); // dir2 is now active

        var active = sut.Workspaces.Single(w => w.RootPath == dir2.FullName);
        WorkspaceSnapshot? activated = null;
        Guid? removed = null;
        sut.WorkspaceActivated += (_, snapshot) => activated = snapshot;
        sut.WorkspaceRemoved += (_, id) => removed = id;

        sut.RemoveWorkspaceCommand.Execute(active);

        Assert.DoesNotContain(sut.Workspaces, w => w.RootPath == dir2.FullName);
        Assert.Equal(active.Id, removed);
        Assert.Equal(dir1.FullName, activated?.RootPath); // switched to the other workspace
        Assert.DoesNotContain(new WorkspaceStateStore(storePath).Load().Workspaces, w => w.Id == active.Id);
    }

    [Fact]
    public void Last_workspace_cannot_be_removed()
    {
        var dir = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var store = new WorkspaceStateStore(Path.Combine(
            Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json"));
        var sut = new WorkspaceListViewModel(store);

        sut.ActivateFolder(dir.FullName);
        var only = sut.Workspaces.Single();

        Assert.False(sut.RemoveWorkspaceCommand.CanExecute(only));
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

    [Fact]
    public void New_workspace_snapshot_defaults_to_stage_mode()
    {
        var snapshot = new WorkspaceSnapshot();

        Assert.True(snapshot.Stage?.IsActive);
        Assert.Equal(PaneKind.Editor, snapshot.Stage?.Pane);
    }

    [Fact]
    public void Workspace_state_preserves_explicit_non_stage_mode()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json");
        var store = new WorkspaceStateStore(path);
        var workspace = new WorkspaceSnapshot
        {
            RootPath = "C:\\work",
            Stage = new StageSnapshot { IsActive = false }
        };

        store.Save(new WorkspaceState { Workspaces = [workspace] });

        var loaded = store.Load().Workspaces.Single();
        Assert.False(loaded.Stage?.IsActive);
        Assert.Null(loaded.Stage?.Pane);
    }
}
