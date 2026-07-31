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

    /// <summary>一覧の選択はカーソル移動でしかない（矢印キーで一覧をたどるたびに切り替わっては困る）。
    /// 切替はコマンド＝クリック／Enter だけで起きる。</summary>
    [Fact]
    public void Selecting_workspace_entry_only_moves_the_cursor()
    {
        var dir1 = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var dir2 = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var store = new WorkspaceStateStore(Path.Combine(
            Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json"));
        var sut = new WorkspaceListViewModel(store);

        sut.ActivateFolder(dir1.FullName);
        sut.ActivateFolder(dir2.FullName);
        var first = sut.Workspaces.Single(w => w.RootPath == dir1.FullName);

        WorkspaceSnapshot? activated = null;
        sut.WorkspaceActivated += (_, snapshot) => activated = snapshot;
        sut.SelectedWorkspace = first;

        Assert.Null(activated);
        Assert.Equal(first, sut.SelectedWorkspace);

        sut.ActivateWorkspaceCommand.Execute(first);

        Assert.Equal(dir1.FullName, activated?.RootPath);
    }

    [Fact]
    public void Pinned_workspaces_sort_above_recent_ones_and_survive_a_reload()
    {
        var dir1 = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var dir2 = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var storePath = Path.Combine(Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json");
        var sut = new WorkspaceListViewModel(new WorkspaceStateStore(storePath));

        sut.ActivateFolder(dir1.FullName);
        sut.ActivateFolder(dir2.FullName); // dir2 の方が新しい＝既定では上
        var older = sut.Workspaces.Single(w => w.RootPath == dir1.FullName);
        Assert.Equal(dir2.FullName, sut.FilteredWorkspaces[0].RootPath);

        sut.TogglePinCommand.Execute(older);

        Assert.Equal(dir1.FullName, sut.FilteredWorkspaces[0].RootPath);

        var reloaded = new WorkspaceListViewModel(new WorkspaceStateStore(storePath));
        Assert.True(reloaded.FilteredWorkspaces[0].IsPinned);
        Assert.Equal(dir1.FullName, reloaded.FilteredWorkspaces[0].RootPath);
    }

    [Fact]
    public void Filter_matches_name_or_path_and_narrows_the_list()
    {
        var root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-ws-{Guid.NewGuid():N}"));
        var alpha = Directory.CreateDirectory(Path.Combine(root.FullName, "alpha"));
        var beta = Directory.CreateDirectory(Path.Combine(root.FullName, "beta"));
        var sut = new WorkspaceListViewModel(new WorkspaceStateStore(Path.Combine(
            Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json")));

        sut.ActivateFolder(alpha.FullName);
        sut.ActivateFolder(beta.FullName);

        sut.Filter = "alph";
        Assert.Equal(alpha.FullName, Assert.Single(sut.FilteredWorkspaces).RootPath);

        // 空白区切りは AND（パスの一部＋名前の一部でも絞れる）
        sut.Filter = "loomo-ws beta";
        Assert.Equal(beta.FullName, Assert.Single(sut.FilteredWorkspaces).RootPath);

        sut.ClearFilterCommand.Execute(null);
        Assert.Equal(2, sut.FilteredWorkspaces.Count);
    }

    [Fact]
    public void Renaming_sets_a_display_name_without_touching_the_folder_name()
    {
        var dir = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var storePath = Path.Combine(Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json");
        var sut = new WorkspaceListViewModel(new WorkspaceStateStore(storePath));
        sut.ActivateFolder(dir.FullName);
        var entry = sut.Workspaces.Single();

        sut.Rename(entry, "  仕事用  ");

        Assert.Equal("仕事用", entry.Label);
        Assert.Equal(dir.Name, entry.Name);
        Assert.True(new WorkspaceListViewModel(new WorkspaceStateStore(storePath))
            .Workspaces.Single().Label == "仕事用");

        sut.Rename(entry, "");   // 空＝既定（フォルダ名）へ戻す
        Assert.Equal(dir.Name, entry.Label);
        Assert.False(entry.HasCustomName);
    }

    /// <summary>ピン留め・表示名は索引側が正。未読込のワークスペースへ切り替えると詳細（state.json）が
    /// 読み込まれて実体が差し替わるので、そこで索引の値を引き継がないと次の保存で消える。</summary>
    [Fact]
    public void Pin_and_name_survive_switching_into_a_workspace_whose_details_were_not_loaded()
    {
        var dir1 = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var dir2 = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-workspace-{Guid.NewGuid():N}"));
        var storePath = Path.Combine(Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json");
        var seed = new WorkspaceListViewModel(new WorkspaceStateStore(storePath));
        seed.ActivateFolder(dir1.FullName);
        seed.ActivateFolder(dir2.FullName);
        seed.Persist();

        // 再起動相当：dir2 だけ詳細が読まれ、dir1 は索引の要約だけ
        var sut = new WorkspaceListViewModel(new WorkspaceStateStore(storePath));
        var unloaded = sut.Workspaces.Single(w => w.RootPath == dir1.FullName);
        sut.TogglePinCommand.Execute(unloaded);
        sut.Rename(unloaded, "旧プロジェクト");

        sut.ActivateWorkspaceCommand.Execute(unloaded);
        sut.Persist();

        var reloaded = new WorkspaceListViewModel(new WorkspaceStateStore(storePath))
            .Workspaces.Single(w => w.RootPath == dir1.FullName);
        Assert.True(reloaded.IsPinned);
        Assert.Equal("旧プロジェクト", reloaded.Label);
    }

    [Fact]
    public void Tab_counts_are_available_for_workspaces_whose_details_are_not_loaded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loomo-store-{Guid.NewGuid():N}");
        var store = new WorkspaceStateStore(Path.Combine(root, "workspaces.json"));
        var active = new WorkspaceSnapshot { RootPath = @"C:\active" };
        var other = new WorkspaceSnapshot
        {
            RootPath = @"C:\other",
            EditorTabs = [new EditorTabSnapshot { FilePath = @"C:\other\a.cs" }],
            TerminalTabs = [new TerminalTabSnapshot(), new TerminalTabSnapshot()],
            BrowserTabs = [new BrowserTabSnapshot { Url = "https://example.com/" }]
        };
        store.Save(new WorkspaceState { ActiveWorkspaceId = active.Id, Workspaces = [active, other] });

        var loaded = store.LoadForStartup().Workspaces.Single(w => w.Id == other.Id);

        Assert.False(loaded.IsDetailsLoaded);
        Assert.Equal((2, 1, 1), (loaded.TabCounts.Terminal, loaded.TabCounts.Editor, loaded.TabCounts.Browser));
    }

    /// <summary>フォルダー一覧はルート＋追加ぶん。追加ぶんは詳細（state.json）を読まなくても
    /// 出せるよう索引側にも載る。行の「🗂」で開いてパスのコピー等ができる。</summary>
    [Fact]
    public void Folder_list_covers_the_root_and_the_additional_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loomo-store-{Guid.NewGuid():N}");
        var storePath = Path.Combine(root, "workspaces.json");
        var store = new WorkspaceStateStore(storePath);
        var active = new WorkspaceSnapshot { RootPath = @"C:\active" };
        var multi = new WorkspaceSnapshot
        {
            RootPath = @"C:\multi",
            AdditionalFolders =
            [
                new WorkspaceFolderPin { FolderPath = @"C:\shared\lib" },
                new WorkspaceFolderPin { FolderPath = @"D:\docs" }
            ]
        };
        store.Save(new WorkspaceState { ActiveWorkspaceId = active.Id, Workspaces = [active, multi] });

        var entry = new WorkspaceListViewModel(new WorkspaceStateStore(storePath))
            .Workspaces.Single(w => w.RootPath == @"C:\multi");

        Assert.True(entry.IsMultiRoot);
        Assert.Equal([@"C:\multi", @"C:\shared\lib", @"D:\docs"], entry.Folders.Select(f => f.Path));
        Assert.Equal([true, false, false], entry.Folders.Select(f => f.IsPrimary));
        Assert.Equal("lib", entry.Folders[1].Name);
    }

    /// <summary>絞り込み欄の隣の「🗂 パス」は一覧全体の表示切替（行ごとの開閉は持たない）。</summary>
    [Fact]
    public void Toggling_folders_shows_every_workspaces_folders_at_once()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loomo-store-{Guid.NewGuid():N}");
        var storePath = Path.Combine(root, "workspaces.json");
        var store = new WorkspaceStateStore(storePath);
        var plain = new WorkspaceSnapshot { RootPath = @"C:\plain" };
        var multi = new WorkspaceSnapshot
        {
            RootPath = @"C:\multi",
            AdditionalFolders = [new WorkspaceFolderPin { FolderPath = @"C:\shared\lib" }]
        };
        store.Save(new WorkspaceState { ActiveWorkspaceId = plain.Id, Workspaces = [plain, multi] });

        var sut = new WorkspaceListViewModel(new WorkspaceStateStore(storePath));
        var multiEntry = sut.Workspaces.Single(w => w.RootPath == @"C:\multi");
        var plainEntry = sut.Workspaces.Single(w => w.RootPath == @"C:\plain");
        Assert.False(sut.ShowFolders);

        // 表示はワークスペースを問わず一括。ルートだけのワークスペースもパスが読める
        sut.ToggleFoldersCommand.Execute(null);
        Assert.True(sut.ShowFolders);
        Assert.Equal([@"C:\multi", @"C:\shared\lib"], multiEntry.Folders.Select(f => f.Path));
        Assert.Equal([@"C:\plain"], plainEntry.Folders.Select(f => f.Path));

        // 「ルート」の印はマルチルートのときだけ（1つしか無い行では区別にならない）
        Assert.True(multiEntry.Folders[0].ShowPrimaryTag);
        Assert.False(plainEntry.Folders[0].ShowPrimaryTag);

        sut.ToggleFoldersCommand.Execute(null);
        Assert.False(sut.ShowFolders);
    }

    /// <summary>非アクティブなワークスペースのフォルダー削除はスナップショットを直接直す
    /// （アクティブなものは生きている FolderTree を通すのでイベントに逃がす）。</summary>
    [Fact]
    public void Removing_a_folder_edits_the_snapshot_when_the_workspace_is_not_active()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loomo-store-{Guid.NewGuid():N}");
        var storePath = Path.Combine(root, "workspaces.json");
        var store = new WorkspaceStateStore(storePath);
        var active = new WorkspaceSnapshot
        {
            RootPath = @"C:\active",
            AdditionalFolders = [new WorkspaceFolderPin { FolderPath = @"C:\active-extra" }]
        };
        var other = new WorkspaceSnapshot
        {
            RootPath = @"C:\other",
            AdditionalFolders = [new WorkspaceFolderPin { FolderPath = @"C:\shared\lib" }]
        };
        store.Save(new WorkspaceState { ActiveWorkspaceId = active.Id, Workspaces = [active, other] });

        var sut = new WorkspaceListViewModel(new WorkspaceStateStore(storePath));
        string? requested = null;
        sut.FolderRemoveRequested += (_, path) => requested = path;

        var otherEntry = sut.Workspaces.Single(w => w.RootPath == @"C:\other");
        sut.RemoveFolder(otherEntry.Folders.Single(f => !f.IsPrimary));

        Assert.Null(requested);
        var reloaded = new WorkspaceListViewModel(new WorkspaceStateStore(storePath))
            .Workspaces.Single(w => w.RootPath == @"C:\other");
        Assert.Equal([@"C:\other"], reloaded.Folders.Select(f => f.Path));   // ルートだけが残る
        Assert.False(reloaded.IsMultiRoot);

        // ルートは取り除けない（それはワークスペースそのものを消すこと）
        sut.RemoveFolder(otherEntry.Folders.Single(f => f.IsPrimary));
        Assert.Null(requested);

        // アクティブなワークスペースぶんは購読側（ShellWindow → WorkspaceService）へ回す
        sut.RemoveFolder(sut.Workspaces.Single(w => w.RootPath == @"C:\active")
            .Folders.Single(f => !f.IsPrimary));
        Assert.Equal(@"C:\active-extra", requested);
    }

    /// <summary>この機能より前に書かれた索引にはタブ数が無い。一覧を開いたときに詳細から一度だけ
    /// 拾い直し、索引へ書き戻す（次回以降は詳細を読まない）。</summary>
    [Fact]
    public void Missing_tab_counts_are_backfilled_from_details_once()
    {
        var root = Path.Combine(Path.GetTempPath(), $"loomo-store-{Guid.NewGuid():N}");
        var storePath = Path.Combine(root, "workspaces.json");
        var store = new WorkspaceStateStore(storePath);
        var active = new WorkspaceSnapshot { RootPath = @"C:\active" };
        var other = new WorkspaceSnapshot
        {
            RootPath = @"C:\other",
            EditorTabs = [new EditorTabSnapshot { FilePath = @"C:\other\a.cs" }],
            TerminalTabs = [new TerminalTabSnapshot()]
        };
        store.Save(new WorkspaceState { ActiveWorkspaceId = active.Id, Workspaces = [active, other] });

        // 索引からタブ数の項目を消して「旧形式」を作る（直前のカンマごと落とす）
        var legacy = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(storePath), ",\\s*\"tabCounts\":\\s*\\{[^}]*\\}", "");
        Assert.DoesNotContain("tabCounts", legacy);
        File.WriteAllText(storePath, legacy);

        var sut = new WorkspaceListViewModel(new WorkspaceStateStore(storePath));
        var entry = sut.Workspaces.Single(w => w.RootPath == @"C:\other");
        Assert.Equal(0, entry.EditorTabCount);

        sut.Refresh();

        Assert.Equal((1, 1), (entry.EditorTabCount, entry.TerminalTabCount));
        Assert.Contains("tabCounts", File.ReadAllText(storePath));
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
