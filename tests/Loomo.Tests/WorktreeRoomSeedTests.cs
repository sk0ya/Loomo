using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>ワークツリーの部屋を今の部屋から写す（§24.17.1）。</summary>
public class WorktreeRoomSeedTests
{
    private const string Main = @"C:\work\app";
    private const string Worktree = @"C:\work\app\.claude\worktrees\fix";
    private const string Sibling = @"C:\work\app\.claude\worktrees\other";

    private static readonly string[] AllWorktrees = [Main, Worktree, Sibling];

    private static WorkspaceSnapshot Source() => new()
    {
        RootPath = Main,
        Name = "app",
        CustomName = "本体",
        Pinned = true,
        EditorTabs =
        [
            new EditorTabSnapshot { FilePath = Main + @"\src\A.cs", CaretLine = 12, CaretColumn = 4, ScrollRatio = 0.3, IsActive = true },
            new EditorTabSnapshot { FilePath = Main + @"\src\Dirty.cs", Text = "未保存の編集", IsModified = true },
            new EditorTabSnapshot { FilePath = Main + @"\src\OnlyInMain.cs" },
            new EditorTabSnapshot { FilePath = null, Text = "名前の無いメモ" },
            new EditorTabSnapshot { FilePath = @"D:\elsewhere\notes.md" },
        ],
        TerminalTabs = [new TerminalTabSnapshot { Id = Guid.NewGuid(), CustomName = "build", WorkingDirectory = Main + @"\src" }],
        BrowserTabs = [new BrowserTabSnapshot { Url = "https://example.com" }],
        TreeExpandedPaths = [Main + @"\src", Main + @"\tests"],
        TreeSelectedPath = Main + @"\src\A.cs",
        PinnedFolders = [Main + @"\docs"],
        AdditionalFolders = [new WorkspaceFolderPin { FolderPath = @"D:\shared" }],
        Pegboard = [new PegboardItemSnapshot { Content = "メモ" }],
        SearchTabs = [new SearchTabSnapshot { Query = "x" }],
        ComposerText = "dotnet test",
        GitCompare = new GitCompareSnapshot { Kind = 1, Branch = "main" },
        DetachedWindows = [new DetachedWindowSnapshot()],
        Mode = DisplayMode.Dock,
        RecentFiles =
        [
            new RecentPathSnapshot { RootIndex = 0, RelativePath = @"src\A.cs" },
            new RecentPathSnapshot { RootIndex = 1, RelativePath = @"lib\B.cs" },
        ],
    };

    private static WorkspaceSnapshot Seed(WorkspaceSnapshot source, string from = Main, string to = Worktree,
        params string[] missing)
        => WorktreeRoomSeed.Seed(source, new WorktreeSwitchRequest(to, from, Main, AllWorktrees),
            path => !missing.Contains(path, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Editor_tabs_follow_to_the_same_relative_path_with_caret_kept()
    {
        var seed = Seed(Source(), missing: Worktree + @"\src\OnlyInMain.cs");

        var a = seed.EditorTabs.Single(t => t.FilePath == Worktree + @"\src\A.cs");
        Assert.Equal(12, a.CaretLine);
        Assert.Equal(4, a.CaretColumn);
        Assert.Equal(0.3, a.ScrollRatio);
        Assert.True(a.IsActive);
        // ワークツリーに無いファイル・名前の無いタブは写さない。外のファイルはそのまま。
        Assert.DoesNotContain(seed.EditorTabs, t => t.FilePath?.EndsWith("OnlyInMain.cs") == true);
        Assert.DoesNotContain(seed.EditorTabs, t => t.FilePath is null);
        Assert.Contains(seed.EditorTabs, t => t.FilePath == @"D:\elsewhere\notes.md");
    }

    /// <summary>未保存の編集は元の部屋のファイルへの編集。ワークツリーのファイルへ持ち込むと別物を上書きする。</summary>
    [Fact]
    public void Unsaved_edits_are_not_carried_into_the_worktree()
    {
        var seed = Seed(Source());

        var dirty = seed.EditorTabs.Single(t => t.FilePath == Worktree + @"\src\Dirty.cs");
        Assert.False(dirty.IsModified);
        Assert.Null(dirty.Text);
    }

    /// <summary>ターミナルのタブ ID は常駐ホストのセッションの鍵（§34）。同じ ID だと元の部屋のシェルを奪い合う。</summary>
    [Fact]
    public void Terminal_tabs_get_new_ids_and_rebased_working_directories()
    {
        var source = Source();
        var oldId = source.TerminalTabs[0].Id;
        source.ActiveTerminalTabId = oldId;
        source.TerminalViewLayout = new ViewportNodeSnapshot { TabId = oldId };

        var seed = Seed(source);

        var tab = Assert.Single(seed.TerminalTabs);
        Assert.NotEqual(oldId, tab.Id);
        Assert.Equal("build", tab.CustomName);
        Assert.Equal(Worktree + @"\src", tab.WorkingDirectory);
        Assert.Equal(tab.Id, seed.ActiveTerminalTabId);
        Assert.Equal(tab.Id, seed.TerminalViewLayout!.TabId);
        Assert.Equal(oldId, source.TerminalTabs[0].Id);   // 元の部屋は書き換えない
    }

    /// <summary>ignore されていて新しいワークツリーには生えていないフォルダー（bin 等）を、シェルの cwd・
    /// ツリーの状態に持ち込まない。</summary>
    [Fact]
    public void Folders_missing_in_the_worktree_are_not_carried()
    {
        var source = Source();
        source.TerminalTabs[0].WorkingDirectory = Main + @"\bin\Debug";
        source.TreeRootPath = Main + @"\bin";
        source.TreeExpandedPaths.Add(Main + @"\bin");

        var seed = Seed(source, missing: [Worktree + @"\bin\Debug", Worktree + @"\bin"]);

        Assert.Equal(Worktree, seed.TerminalTabs[0].WorkingDirectory);
        Assert.Null(seed.TreeRootPath);
        Assert.DoesNotContain(Worktree + @"\bin", seed.TreeExpandedPaths);
    }

    [Fact]
    public void Tree_state_is_rebased_and_room_identity_is_new()
    {
        var source = Source();
        var seed = Seed(source);

        Assert.NotEqual(source.Id, seed.Id);
        Assert.Equal(Worktree, seed.RootPath);
        Assert.Equal("fix", seed.Name);
        Assert.Null(seed.CustomName);
        Assert.False(seed.Pinned);
        Assert.Equal([Worktree + @"\src", Worktree + @"\tests"], seed.TreeExpandedPaths);
        Assert.Equal(Worktree + @"\src\A.cs", seed.TreeSelectedPath);
        Assert.Equal([Worktree + @"\docs"], seed.PinnedFolders);
        Assert.Equal(@"D:\shared", Assert.Single(seed.AdditionalFolders).FolderPath);
        Assert.Equal(DisplayMode.Dock, seed.Mode);
        Assert.NotEqual(source.BrowserTabs[0].Id, seed.BrowserTabs[0].Id);
        Assert.Equal("https://example.com", seed.BrowserTabs[0].Url);
    }

    [Fact]
    public void What_the_source_room_is_doing_right_now_stays_behind()
    {
        var seed = Seed(Source());

        Assert.Empty(seed.Pegboard);
        Assert.Empty(seed.SearchTabs);
        Assert.Empty(seed.DetachedWindows);
        Assert.Null(seed.ComposerText);
        Assert.Null(seed.GitCompare);
    }

    /// <summary>ワークツリーの部屋から本体へ写す逆向き。どのパスも本体の下にあるが、持ち主はワークツリー
    /// なので付け替える（「本体の下ならそのまま」だと、本体の部屋がワークツリーのファイルを開いてしまう）。</summary>
    [Fact]
    public void Seeding_the_main_room_from_a_worktree_room_moves_paths_back_out()
    {
        var source = new WorkspaceSnapshot
        {
            RootPath = Worktree,
            EditorTabs = [new EditorTabSnapshot { FilePath = Worktree + @"\src\A.cs" }],
            TerminalTabs = [new TerminalTabSnapshot { Id = Guid.NewGuid(), WorkingDirectory = Worktree + @"\src" }],
            TreeExpandedPaths = [Worktree + @"\src"],
            TreeSelectedPath = Worktree + @"\src\A.cs",
        };

        var seed = Seed(source, from: Worktree, to: Main);

        Assert.Equal(Main + @"\src\A.cs", Assert.Single(seed.EditorTabs).FilePath);
        Assert.Equal(Main + @"\src", seed.TerminalTabs[0].WorkingDirectory);
        Assert.Equal([Main + @"\src"], seed.TreeExpandedPaths);
        Assert.Equal(Main + @"\src\A.cs", seed.TreeSelectedPath);
    }

    /// <summary>本体の部屋で兄弟のワークツリーのファイルを開いていても、それは本体のものではない。</summary>
    [Fact]
    public void Paths_in_a_sibling_worktree_are_left_alone()
    {
        var source = Source();
        source.EditorTabs.Add(new EditorTabSnapshot { FilePath = Sibling + @"\src\S.cs" });
        source.EditorTabs.Add(new EditorTabSnapshot { FilePath = Worktree + @"\src\T.cs" });

        var seed = Seed(source);

        Assert.Contains(seed.EditorTabs, t => t.FilePath == Sibling + @"\src\S.cs");
        Assert.Contains(seed.EditorTabs, t => t.FilePath == Worktree + @"\src\T.cs");
        Assert.DoesNotContain(seed.EditorTabs, t => t.FilePath!.Contains(@"fix\.claude"));
    }

    /// <summary>マルチルートで Git の対象が追加フォルダー側にあるとき、差し替わるのはそのフォルダーで、
    /// 主フォルダー（ドキュメント等）は追加フォルダーとして残る。最近の項目のルート番号も振り直す。</summary>
    [Fact]
    public void Multi_root_replaces_the_folder_that_holds_the_repository()
    {
        const string docs = @"C:\proj\docs";
        const string app = @"C:\proj\app";
        const string appWorktree = @"C:\proj\app\.claude\worktrees\x";
        var source = new WorkspaceSnapshot
        {
            RootPath = docs,
            PinnedFolders = [docs + @"\guide"],
            AdditionalFolders = [new WorkspaceFolderPin { FolderPath = app, PinnedFolders = [app + @"\src"] }],
            EditorTabs =
            [
                new EditorTabSnapshot { FilePath = docs + @"\README.md" },
                new EditorTabSnapshot { FilePath = app + @"\src\A.cs" },
            ],
            RecentFiles =
            [
                new RecentPathSnapshot { RootIndex = 0, RelativePath = "README.md" },
                new RecentPathSnapshot { RootIndex = 1, RelativePath = @"src\A.cs" },
            ],
        };

        var seed = WorktreeRoomSeed.Seed(source,
            new WorktreeSwitchRequest(appWorktree, app, app, [app, appWorktree]), _ => true);

        Assert.Equal(appWorktree, seed.RootPath);
        Assert.Equal([appWorktree + @"\src"], seed.PinnedFolders);
        var kept = Assert.Single(seed.AdditionalFolders);
        Assert.Equal(docs, kept.FolderPath);
        Assert.Equal([docs + @"\guide"], kept.PinnedFolders);
        Assert.Contains(seed.EditorTabs, t => t.FilePath == appWorktree + @"\src\A.cs");
        Assert.Contains(seed.EditorTabs, t => t.FilePath == docs + @"\README.md");
        Assert.Contains(seed.RecentFiles, r => r.RootIndex == 0 && r.RelativePath == @"src\A.cs");
        Assert.Contains(seed.RecentFiles, r => r.RootIndex == 1 && r.RelativePath == "README.md");
    }

    [Fact]
    public void Recent_items_are_reindexed_when_folders_drop_out()
    {
        var source = Source();
        // 追加フォルダーの1つ目がワークツリーと親子関係になる（＝持ち越せない）と、後ろの番号が詰まる。
        source.AdditionalFolders.Insert(0, new WorkspaceFolderPin { FolderPath = @"C:\work\app\.claude\worktrees" });
        source.RecentFiles = [new RecentPathSnapshot { RootIndex = 2, RelativePath = @"x\y.txt" }];

        var seed = Seed(source);

        var recent = Assert.Single(seed.RecentFiles);
        Assert.Equal(1, recent.RootIndex);
        Assert.Equal(@"D:\shared", seed.AdditionalFolders[recent.RootIndex - 1].FolderPath);
    }
}

/// <summary>ワークツリーの部屋の出入り（§24.17.1）。</summary>
public class WorktreeRoomListTests
{
    private static string NewDir(string? under = null)
        => Directory.CreateDirectory(Path.Combine(under ?? Path.GetTempPath(), $"loomo-wt-{Guid.NewGuid():N}")).FullName;

    private static WorkspaceListViewModel NewList()
        => new(new WorkspaceStateStore(Path.Combine(Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json")));

    private static WorktreeSwitchRequest To(string target, string from, string main, params string[] others)
        => new(target, from, main, [main, target, from, .. others]);

    [Fact]
    public void First_switch_seeds_from_the_current_room_and_the_second_returns_to_it()
    {
        var main = NewDir();
        var worktree = NewDir(main);
        var sut = NewList();
        var activated = new List<WorkspaceSnapshot>();
        sut.WorkspaceActivated += (_, s) => activated.Add(s);
        sut.ActivateFolder(main);
        var mainRoom = activated[^1];
        mainRoom.EditorTabs.Add(new EditorTabSnapshot { FilePath = Path.Combine(main, "a.txt") });

        sut.ActivateWorktree(To(worktree, main, main), mainRoom, _ => true);
        var worktreeRoom = activated[^1];
        Assert.Equal(worktree, worktreeRoom.RootPath);
        Assert.Equal(main, worktreeRoom.WorktreeOf);
        Assert.Equal(Path.Combine(worktree, "a.txt"), Assert.Single(worktreeRoom.EditorTabs).FilePath);

        // 本体へ戻ってから同じワークツリーへ：写し直さず、前の部屋へ戻る。
        sut.ActivateWorktree(To(main, worktree, main), worktreeRoom, _ => true);
        Assert.Equal(mainRoom.Id, activated[^1].Id);
        sut.ActivateWorktree(To(worktree, main, main), activated[^1], _ => true);
        Assert.Equal(worktreeRoom.Id, activated[^1].Id);
        Assert.Equal(2, sut.Workspaces.Count);
    }

    /// <summary>ワークツリーの部屋から先に開いた本体は、どこの「写し」でもない。</summary>
    [Fact]
    public void Main_worktree_seeded_from_a_worktree_room_is_not_nested()
    {
        var main = NewDir();
        var worktree = NewDir(main);
        var sut = NewList();
        WorkspaceSnapshot? last = null;
        sut.WorkspaceActivated += (_, s) => last = s;
        sut.ActivateFolder(worktree);

        sut.ActivateWorktree(To(main, worktree, main), last, _ => true);

        Assert.Equal(main, last!.RootPath);
        Assert.Null(last.WorktreeOf);
    }

    /// <summary>普通の部屋として開いていたワークツリー A から B へ写しても、畳み先は A ではなく本体。</summary>
    [Fact]
    public void Worktree_rooms_fold_under_the_main_worktree_whatever_room_they_were_seeded_from()
    {
        var main = NewDir();
        var a = NewDir(main);
        var b = NewDir(main);
        var sut = NewList();
        WorkspaceSnapshot? last = null;
        sut.WorkspaceActivated += (_, s) => last = s;
        sut.ActivateFolder(a);

        sut.ActivateWorktree(To(b, a, main), last, _ => true);

        Assert.Equal(main, last!.WorktreeOf);
        // 本体の部屋は後から作られても、その下に畳まれる（目印は部屋の Id ではなくパス）。
        sut.ActivateFolder(main);
        var rows = sut.FilteredWorkspaces.Select(w => (w.RootPath, w.IsNested)).ToList();
        Assert.Equal((main, false), rows[0]);
        Assert.Contains((b, true), rows);
    }

    [Fact]
    public void Worktree_rooms_fold_under_their_main_room_in_the_list()
    {
        var main = NewDir();
        var other = NewDir();
        var worktree = NewDir(main);
        var sut = NewList();
        WorkspaceSnapshot? last = null;
        sut.WorkspaceActivated += (_, s) => last = s;
        sut.ActivateFolder(main);
        sut.ActivateWorktree(To(worktree, main, main), last, _ => true);
        sut.ActivateFolder(other);   // いちばん新しい（＝本来は先頭に来る）

        var rows = sut.FilteredWorkspaces.Select(w => (w.RootPath, w.IsNested)).ToList();
        Assert.Equal([(other, false), (main, false), (worktree, true)], rows);

        // 本体が絞り込みで外れたら、ワークツリーの部屋は上の階層へ出る。
        sut.Filter = Path.GetFileName(worktree);
        var row = Assert.Single(sut.FilteredWorkspaces);
        Assert.False(row.IsNested);
    }

    /// <summary>Claude Code などが外でワークツリーを消したら、写しの部屋は一覧から外れる（普通の部屋は残す）。</summary>
    [Fact]
    public void Vanished_worktree_rooms_are_pruned_but_plain_rooms_are_kept()
    {
        var main = NewDir();
        var plain = NewDir();
        var worktree = NewDir(main);
        var sut = NewList();
        WorkspaceSnapshot? last = null;
        sut.WorkspaceActivated += (_, s) => last = s;
        sut.ActivateFolder(plain);
        sut.ActivateFolder(main);
        sut.ActivateWorktree(To(worktree, main, main), last, _ => true);
        sut.ActivateFolder(main);
        var removed = new List<Guid>();
        sut.WorkspaceRemoved += (_, id) => removed.Add(id);

        Directory.Delete(worktree);
        Directory.Delete(plain);
        sut.Refresh();

        Assert.DoesNotContain(sut.Workspaces, w => w.RootPath == worktree);
        Assert.Contains(sut.Workspaces, w => w.RootPath == plain && w.IsMissing);
        Assert.Single(removed);
    }

    /// <summary>本体ごと見えない（ドライブが一時的に外れた）なら、ワークツリーは消えたのではない。</summary>
    [Fact]
    public void Worktree_rooms_are_kept_while_the_main_worktree_is_unreachable()
    {
        var main = NewDir();
        var worktree = NewDir(main);
        var sut = NewList();
        WorkspaceSnapshot? last = null;
        sut.WorkspaceActivated += (_, s) => last = s;
        var elsewhere = NewDir();
        sut.ActivateFolder(main);
        sut.ActivateWorktree(To(worktree, main, main), last, _ => true);
        sut.ActivateFolder(elsewhere);

        Directory.Delete(main, recursive: true);
        sut.Refresh();

        Assert.Contains(sut.Workspaces, w => w.RootPath == worktree);
    }

    [Fact]
    public void Removing_a_worktree_drops_its_room_unless_it_is_the_open_one()
    {
        var main = NewDir();
        var worktree = NewDir(main);
        var sut = NewList();
        WorkspaceSnapshot? last = null;
        sut.WorkspaceActivated += (_, s) => last = s;
        sut.ActivateFolder(main);
        sut.ActivateWorktree(To(worktree, main, main), last, _ => true);

        sut.RemoveWorktreeRoom(worktree);   // いま開いている部屋は消さない
        Assert.Equal(2, sut.Workspaces.Count);

        sut.ActivateFolder(main);
        sut.RemoveWorktreeRoom(worktree);
        Assert.Single(sut.Workspaces);
    }

    [Fact]
    public void Worktree_link_survives_a_reload_of_the_index()
    {
        var storePath = Path.Combine(Path.GetTempPath(), $"loomo-workspaces-{Guid.NewGuid():N}.json");
        var main = NewDir();
        var worktree = NewDir(main);
        var first = new WorkspaceListViewModel(new WorkspaceStateStore(storePath));
        WorkspaceSnapshot? last = null;
        first.WorkspaceActivated += (_, s) => last = s;
        first.ActivateFolder(main);
        first.ActivateWorktree(To(worktree, main, main), last, _ => true);
        first.Persist();

        var reloaded = new WorkspaceListViewModel(new WorkspaceStateStore(storePath));

        Assert.Equal(main, reloaded.Workspaces.Single(w => w.RootPath == worktree).WorktreeOf);
    }
}
