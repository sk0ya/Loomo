using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

public sealed class RecentUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-recent-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside;

    public RecentUsageTests()
    {
        Directory.CreateDirectory(_root);
        _outside = Path.Combine(Path.GetTempPath(), "loomo-recent-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outside);
    }

    [Fact]
    public void ファイルは相対パスで重複排除され最近順に上限まで保持する()
    {
        var file = Path.Combine(_root, "src", "main.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "class C {}");
        var workspace = Snapshot();
        var service = new RecentUsageService();

        Assert.True(service.RecordFile(workspace, file, DateTime.UnixEpoch.AddMinutes(1)));
        Assert.True(service.RecordFile(workspace, file, DateTime.UnixEpoch.AddMinutes(2)));
        for (var i = 0; i < RecentUsageService.MaxRecentFiles + 3; i++)
        {
            var path = Path.Combine(_root, $"file-{i}.txt");
            File.WriteAllText(path, "x");
            service.RecordFile(workspace, path, DateTime.UnixEpoch.AddMinutes(10 + i));
        }

        var loaded = service.Load(workspace);
        Assert.Equal(RecentUsageService.MaxRecentFiles, loaded.Files.Count);
        Assert.DoesNotContain(loaded.Files, x => x.RelativePath == Path.Combine("src", "main.cs"));
        var latest = Assert.Single(loaded.Files, x => x.RelativePath == "file-22.txt");
        Assert.Equal(0, latest.RootIndex);
        Assert.Equal(1, latest.UseCount);
    }

    [Fact]
    public void フォルダーは頻度順で同一フォルダーの利用回数を集計する()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
        var workspace = Snapshot();
        var service = new RecentUsageService();

        service.RecordFolder(workspace, a, DateTime.UnixEpoch.AddMinutes(1));
        service.RecordFolder(workspace, a, DateTime.UnixEpoch.AddMinutes(2));
        service.RecordFolder(workspace, b, DateTime.UnixEpoch.AddMinutes(3));

        var folders = service.Load(workspace).Folders;
        Assert.Equal(2, folders.Count);
        Assert.Equal("a", folders[0].RelativePath);
        Assert.Equal(2, folders[0].UseCount);
    }

    [Fact]
    public void ワークスペース外と存在しない項目は記録も表示もしない()
    {
        var workspace = Snapshot();
        var service = new RecentUsageService();
        var outsideFile = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(outsideFile, "not stored");

        Assert.False(service.RecordFile(workspace, outsideFile));
        Assert.False(service.RecordFolder(workspace, _outside));
        workspace.RecentFiles.Add(new RecentPathSnapshot { RootIndex = 0, RelativePath = "missing.txt" });
        Assert.Empty(service.Load(workspace).Files);
    }

    [Fact]
    public void 追加ルートはルート番号と相対パスで復元できる()
    {
        var secondary = Directory.CreateDirectory(Path.Combine(_outside, "secondary")).FullName;
        var file = Path.Combine(secondary, "note.md");
        File.WriteAllText(file, "note");
        var workspace = Snapshot();
        workspace.AdditionalFolders.Add(new WorkspaceFolderPin { FolderPath = secondary });
        var service = new RecentUsageService();

        Assert.True(service.RecordFile(workspace, file));
        var saved = Assert.Single(workspace.RecentFiles);
        Assert.Equal(1, saved.RootIndex);
        Assert.Equal("note.md", saved.RelativePath);
        Assert.Equal(file, RecentUsageService.Resolve(workspace, saved));
    }

    [Fact]
    public void 永続化された親相対パスはワークスペース外へ解決しない()
    {
        var outsideFile = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(outsideFile, "secret");
        var workspace = Snapshot();
        workspace.RecentFiles.Add(new RecentPathSnapshot
        {
            RootIndex = 0,
            RelativePath = Path.Combine("..", Path.GetFileName(_outside), "secret.txt"),
        });
        var persisted = workspace.RecentFiles[0];

        var service = new RecentUsageService();

        Assert.Empty(service.Load(workspace).Files);
        Assert.Equal("", RecentUsageService.Resolve(workspace, persisted));
    }

    [Fact]
    public void 既存の重複スナップショットは利用回数を統合する()
    {
        var file = Path.Combine(_root, "duplicate.txt");
        File.WriteAllText(file, "x");
        var workspace = Snapshot();
        workspace.FrequentFolders.Add(new RecentPathSnapshot
        {
            RootIndex = 0, RelativePath = "", UseCount = 2,
            LastUsedUtc = DateTime.UnixEpoch.AddMinutes(1),
        });
        workspace.FrequentFolders.Add(new RecentPathSnapshot
        {
            RootIndex = 0, RelativePath = "", UseCount = 3,
            LastUsedUtc = DateTime.UnixEpoch.AddMinutes(2),
        });

        var folder = Assert.Single(new RecentUsageService().Load(workspace).Folders);

        Assert.Equal(5, folder.UseCount);
        Assert.Single(workspace.FrequentFolders);
    }

    [Fact]
    public void 表示場所は項目自身を繰り返さず親フォルダーを返す()
    {
        var rootFile = Path.Combine(_root, "README.md");
        var nestedFile = Path.Combine(_root, "src", "main.cs");
        var nestedFolder = Path.Combine(_root, "src", "components");
        Directory.CreateDirectory(Path.GetDirectoryName(nestedFile)!);
        Directory.CreateDirectory(nestedFolder);
        File.WriteAllText(rootFile, "readme");
        File.WriteAllText(nestedFile, "class C {}");
        var workspace = Snapshot();

        var rootFileItem = new RecentPathSnapshot { RootIndex = 0, RelativePath = "README.md" };
        var nestedFileItem = new RecentPathSnapshot
        {
            RootIndex = 0,
            RelativePath = Path.Combine("src", "main.cs"),
        };
        var nestedFolderItem = new RecentPathSnapshot
        {
            RootIndex = 0,
            RelativePath = Path.Combine("src", "components"),
        };

        Assert.Equal("ワークスペース直下",
            RecentUsageService.LocationLabel(workspace, rootFileItem, isDirectory: false));
        Assert.Equal("src",
            RecentUsageService.LocationLabel(workspace, nestedFileItem, isDirectory: false));
        Assert.Equal("src",
            RecentUsageService.LocationLabel(workspace, nestedFolderItem, isDirectory: true));
    }

    [Fact]
    public async Task 非同期履歴更新はキャンセルできる()
    {
        var workspace = Snapshot();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RecentUsageService().LoadAsync(workspace, cancellation.Token));
    }

    [Fact]
    public void 記録は共有リストを書き換えず入れ替える()
    {
        // 記録はスレッドプールで走る一方、同じ WorkspaceSnapshot を保存側が直列化しうる。
        // その場で Clear()／AddRange したり既存要素を書き換えたりすると保存が落ちるので、
        // 「元のリストと要素はそのまま・参照だけ差し替える」ことを守らせる。
        var first = Path.Combine(_root, "one.txt");
        var second = Path.Combine(_root, "two.txt");
        File.WriteAllText(first, "1");
        File.WriteAllText(second, "2");
        var workspace = Snapshot();
        var service = new RecentUsageService();

        service.RecordFile(workspace, first, DateTime.UnixEpoch.AddMinutes(1));
        var before = workspace.RecentFiles;
        var beforeEntry = Assert.Single(before);
        var beforeUsedAt = beforeEntry.LastUsedUtc;

        service.RecordFile(workspace, second, DateTime.UnixEpoch.AddMinutes(2));
        service.RecordFile(workspace, first, DateTime.UnixEpoch.AddMinutes(3));

        Assert.NotSame(before, workspace.RecentFiles);
        Assert.Single(before);                                  // 直列化中の並びは変えられていない
        Assert.Equal(beforeUsedAt, beforeEntry.LastUsedUtc);    // 既存要素も書き換えていない
        Assert.Equal(2, workspace.RecentFiles.Count);
        Assert.Equal("one.txt", workspace.RecentFiles[0].RelativePath);
        Assert.Equal(DateTime.UnixEpoch.AddMinutes(3), workspace.RecentFiles[0].LastUsedUtc);
        Assert.Equal(2, workspace.RecentFiles[0].UseCount);
    }

    private WorkspaceSnapshot Snapshot() => new() { RootPath = _root, Name = "test" };

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        try { if (Directory.Exists(_outside)) Directory.Delete(_outside, true); } catch { }
    }
}
