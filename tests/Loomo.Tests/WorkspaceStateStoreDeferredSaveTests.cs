using System.IO;
using System.Linq;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 定期保存はディスクへの書き出しを書き出しスレッドへ回す（§31.15）。回しても、読む側から見えるものは
/// 同期保存と同じでなければならない——ここで固定するのはその等価性と、未保存本文（下書き）の扱い。
/// </summary>
public sealed class WorkspaceStateStoreDeferredSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"loomo-store-{Guid.NewGuid():N}");

    public WorkspaceStateStoreDeferredSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string StatePath => Path.Combine(_dir, "workspaces.json");

    private static WorkspaceState StateWith(params EditorTabSnapshot[] tabs)
    {
        var workspace = new WorkspaceSnapshot { RootPath = @"C:\work", Name = "work" };
        workspace.EditorTabs.AddRange(tabs);
        return new WorkspaceState { Workspaces = [workspace], ActiveWorkspaceId = workspace.Id };
    }

    [Fact]
    public void A_deferred_save_is_on_disk_once_it_has_been_flushed()
    {
        using var store = new WorkspaceStateStore(StatePath);
        var state = StateWith(new EditorTabSnapshot
        {
            FilePath = @"C:\work\dirty.cs", Text = "未保存の本文", IsModified = true, IsActive = true
        });

        store.SaveDeferred(state);
        store.Flush();

        var reloaded = new WorkspaceStateStore(StatePath).Load();
        var tab = Assert.Single(reloaded.Workspaces.Single().EditorTabs);
        Assert.Equal("未保存の本文", tab.Text);
        Assert.True(tab.IsModified);
    }

    [Fact]
    public void Reading_back_through_the_same_store_does_not_need_an_explicit_flush()
    {
        using var store = new WorkspaceStateStore(StatePath);
        var state = StateWith(new EditorTabSnapshot
        {
            FilePath = @"C:\work\dirty.cs", Text = "あとで読む", IsModified = true
        });

        store.SaveDeferred(state);

        // Load は自分で待つ（read-after-write の約束はここでも保たれる）
        var tab = Assert.Single(store.Load().Workspaces.Single().EditorTabs);
        Assert.Equal("あとで読む", tab.Text);
    }

    [Fact]
    public void A_deferred_save_and_a_synchronous_save_leave_the_same_files()
    {
        var deferredDir = Path.Combine(_dir, "deferred");
        var syncDir = Path.Combine(_dir, "sync");
        Directory.CreateDirectory(deferredDir);
        Directory.CreateDirectory(syncDir);
        var id = Guid.NewGuid();
        var tabId = Guid.NewGuid();

        EditorTabSnapshot Tab() => new()
        {
            Id = tabId, FilePath = @"C:\work\dirty.cs", Text = "本文", IsModified = true, IsActive = true
        };
        WorkspaceState State() => new()
        {
            Workspaces =
            [
                new WorkspaceSnapshot
                {
                    Id = id, RootPath = @"C:\work", Name = "work",
                    EditorTabs = [Tab()],
                }
            ],
            ActiveWorkspaceId = id,
        };

        using (var deferred = new WorkspaceStateStore(Path.Combine(deferredDir, "workspaces.json")))
        {
            deferred.SaveDeferred(State());
            deferred.Flush();
        }
        using (var sync = new WorkspaceStateStore(Path.Combine(syncDir, "workspaces.json")))
        {
            sync.Save(State());
        }

        Assert.Equal(RelativeFiles(syncDir), RelativeFiles(deferredDir));
        foreach (var relative in RelativeFiles(syncDir))
            Assert.Equal(
                Normalize(File.ReadAllText(Path.Combine(syncDir, relative))),
                Normalize(File.ReadAllText(Path.Combine(deferredDir, relative))));
    }

    /// <summary>保存ごとに変わる項目（最終利用時刻）は比べない。</summary>
    private static string Normalize(string json) => string.Join('\n',
        json.Split('\n').Where(line => !line.Contains("lastUsedUtc")));

    private static List<string> RelativeFiles(string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToList();

    [Fact]
    public void A_saved_tab_keeps_no_draft_and_needs_no_text()
    {
        // 保存済みのタブは本文を持たずに永続化される（CaptureEditorTab が読むのをやめた前提）。
        // 復元はファイルから読み直すので、ここで落ちるものは無い。
        using var store = new WorkspaceStateStore(StatePath);
        var tabId = Guid.NewGuid();
        var dirty = StateWith(new EditorTabSnapshot
        {
            Id = tabId, FilePath = @"C:\work\a.cs", Text = "編集中", IsModified = true
        });
        store.Save(dirty);
        var draft = Directory.EnumerateFiles(_dir, "*.txt", SearchOption.AllDirectories).Single();
        Assert.Equal("編集中", File.ReadAllText(draft));

        var saved = StateWith(new EditorTabSnapshot
        {
            Id = tabId, FilePath = @"C:\work\a.cs", Text = null, IsModified = false
        });
        saved.Workspaces[0].Id = dirty.Workspaces[0].Id;
        store.SaveDeferred(saved);
        store.Flush();

        Assert.False(File.Exists(draft));
        var reloaded = new WorkspaceStateStore(StatePath).Load()
            .Workspaces.Single().EditorTabs.Single();
        Assert.Equal(@"C:\work\a.cs", reloaded.FilePath);
        Assert.False(reloaded.IsModified);
    }

    [Fact]
    public void Deleting_a_workspace_is_not_undone_by_a_save_that_was_still_queued()
    {
        using var store = new WorkspaceStateStore(StatePath);
        var state = StateWith(new EditorTabSnapshot
        {
            FilePath = @"C:\work\dirty.cs", Text = "本文", IsModified = true
        });
        var id = state.Workspaces[0].Id;

        store.SaveDeferred(state);
        store.DeleteWorkspace(id);

        Assert.False(Directory.Exists(Path.Combine(_dir, "workspaces", id.ToString("N"))));
    }
}
