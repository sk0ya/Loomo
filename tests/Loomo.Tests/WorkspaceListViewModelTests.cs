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
    public void Workspace_state_round_trips_detached_windows_per_workspace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json");
        var store = new WorkspaceStateStore(path);
        var first = new WorkspaceSnapshot
        {
            RootPath = @"C:\first",
            DetachedWindows =
            [
                new DetachedWindowSnapshot
                {
                    Left = 120, Top = 80, Width = 1100, Height = 720, IsMaximized = true,
                    ActiveItemIndex = 1,
                    Items =
                    [
                        new DetachedItemSnapshot { Kind = "TerminalSpinoff", WorkingDirectory = @"C:\first\src" },
                        new DetachedItemSnapshot { Kind = "BrowserSpinoff", Url = "https://example.com/" }
                    ]
                }
            ]
        };
        var second = new WorkspaceSnapshot { RootPath = @"C:\second" };

        store.Save(new WorkspaceState { ActiveWorkspaceId = first.Id, Workspaces = [first, second] });

        var loadedFirst = store.LoadWorkspace(first.Id)!;
        var window = Assert.Single(loadedFirst.DetachedWindows);
        Assert.Equal((120, 80, 1100, 720), (window.Left, window.Top, window.Width, window.Height));
        Assert.True(window.IsMaximized);
        Assert.Equal(1, window.ActiveItemIndex);
        Assert.Equal(@"C:\first\src", window.Items[0].WorkingDirectory);
        Assert.Equal("https://example.com/", window.Items[1].Url);
        Assert.Empty(store.LoadWorkspace(second.Id)!.DetachedWindows);
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

    [Fact]
    public void Store_splits_workspace_details_and_defers_unsaved_text()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loomo-store-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "workspaces.json");
        var store = new WorkspaceStateStore(path);
        var workspace = new WorkspaceSnapshot
        {
            RootPath = @"C:\work",
            EditorTabs =
            [
                new EditorTabSnapshot
                {
                    FilePath = @"C:\work\draft.txt",
                    Text = "unsaved body",
                    IsModified = true
                },
                new EditorTabSnapshot
                {
                    FilePath = @"C:\work\clean.txt",
                    Text = "clean body",
                    IsModified = false
                }
            ]
        };

        store.Save(new WorkspaceState
            { ActiveWorkspaceId = workspace.Id, Workspaces = [workspace] });

        var indexJson = File.ReadAllText(path);
        var workspaceDir = Path.Combine(root, "workspaces", workspace.Id.ToString("N"));
        var stateJson = File.ReadAllText(Path.Combine(workspaceDir, "state.json"));
        Assert.DoesNotContain("unsaved body", indexJson);
        Assert.DoesNotContain("unsaved body", stateJson);
        Assert.DoesNotContain("clean body", stateJson);
        Assert.Single(Directory.GetFiles(Path.Combine(workspaceDir, "drafts"), "*.txt"));

        var startup = store.LoadForStartup().Workspaces.Single();
        var draft = startup.EditorTabs[0];
        Assert.Null(draft.Text);
        Assert.Equal("unsaved body", draft.LoadText());
        Assert.Equal("unsaved body", store.Load().Workspaces.Single().EditorTabs[0].Text);
    }

    [Fact]
    public void Startup_loads_only_active_workspace_details()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loomo-store-{Guid.NewGuid():N}");
        var store = new WorkspaceStateStore(Path.Combine(root, "workspaces.json"));
        var active = new WorkspaceSnapshot { RootPath = @"C:\active", ComposerText = "active detail" };
        var inactive = new WorkspaceSnapshot { RootPath = @"C:\inactive", ComposerText = "inactive detail" };
        store.Save(new WorkspaceState
            { ActiveWorkspaceId = active.Id, Workspaces = [active, inactive] });

        var startup = store.LoadForStartup();

        Assert.Equal("active detail", startup.Workspaces.Single(w => w.Id == active.Id).ComposerText);
        Assert.Null(startup.Workspaces.Single(w => w.Id == inactive.Id).ComposerText);
        Assert.Equal("inactive detail", store.LoadWorkspace(inactive.Id)?.ComposerText);
    }

    [Fact]
    public void Legacy_single_editor_unsaved_text_is_migrated_to_a_draft_immediately()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loomo-store-{Guid.NewGuid():N}");
        var store = new WorkspaceStateStore(Path.Combine(root, "workspaces.json"));
        var workspace = new WorkspaceSnapshot
        {
            RootPath = @"C:\work",
            Editor = new EditorSnapshot
            {
                FilePath = @"C:\work\legacy.txt",
                Text = "legacy unsaved body",
                IsModified = true
            }
        };

        store.Save(new WorkspaceState
            { ActiveWorkspaceId = workspace.Id, Workspaces = [workspace] });

        var workspaceDir = Path.Combine(root, "workspaces", workspace.Id.ToString("N"));
        var draft = Assert.Single(Directory.GetFiles(Path.Combine(workspaceDir, "drafts"), "*.txt"));
        Assert.Equal("legacy unsaved body", File.ReadAllText(draft));

        var loaded = store.LoadForStartup().Workspaces.Single();
        var tab = Assert.Single(loaded.EditorTabs);
        Assert.True(tab.IsModified);
        Assert.Equal("legacy unsaved body", tab.LoadText());
    }
}
