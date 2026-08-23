using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class ShellFileOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-shell-" + Guid.NewGuid().ToString("N"));

    public ShellFileOperationsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void 複数選択は各パスをShellのFileNameへ渡し引数文字列を作らない()
    {
        var first = Path.Combine(_root, "a & [quote].txt");
        var second = Path.Combine(_root, "second file.txt");
        File.WriteAllText(first, "a");
        File.WriteAllText(second, "b");
        var starts = new List<ProcessStartInfo>();
        var service = new ShellFileOperations(info => { starts.Add(info); return true; });

        var result = service.Execute(ShellFileAction.OpenWith, [first, second, first]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, starts.Count);
        Assert.Equal("openas", starts[0].Verb);
        Assert.All(starts, info => Assert.Empty(info.Arguments));
        Assert.Contains(Path.GetFullPath(first), starts.Select(info => info.FileName));
        Assert.Contains(Path.GetFullPath(second), starts.Select(info => info.FileName));
    }

    [Fact]
    public void 存在しないパスはShellへ渡さず失敗として返す()
    {
        var called = false;
        var service = new ShellFileOperations(_ => { called = true; return true; });

        var result = service.Execute(ShellFileAction.Share, [Path.Combine(_root, "missing.txt")]);

        Assert.False(called);
        Assert.Empty(result.SucceededPaths);
        Assert.Single(result.FailedPaths);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "missing.txt")), result.FailedPaths[0]);
        Assert.False(result.IsCancelled);
    }

    [Fact]
    public void キャンセル済みならShellを起動しない()
    {
        var path = Path.Combine(_root, "cancel.txt");
        File.WriteAllText(path, "x");
        var called = false;
        var service = new ShellFileOperations(_ => { called = true; return true; });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = service.Execute(ShellFileAction.SendTo, [path], cts.Token);

        Assert.True(result.IsCancelled);
        Assert.False(called);
    }
}

public sealed class ZipFileOperationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-zip-" + Guid.NewGuid().ToString("N"));

    public ZipFileOperationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void 複数選択のZIPは一つの履歴としてUndoRedoできる()
    {
        var workspace = new WorkspaceService(new SafetySettings());
        workspace.OpenFolder(_root);
        var history = new FileOperationHistory();
        var commands = new FolderTreeCommandHandler(workspace, history);
        var first = Path.Combine(_root, "one.txt");
        var folder = Path.Combine(_root, "folder");
        var nested = Path.Combine(folder, "two.txt");
        Directory.CreateDirectory(folder);
        File.WriteAllText(first, "one");
        File.WriteAllText(nested, "two");

        var zipPath = commands.CompressToZip([first, folder]);

        Assert.True(File.Exists(zipPath));
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            Assert.Contains(archive.Entries, e => e.FullName == "one.txt");
            Assert.Contains(archive.Entries, e => e.FullName == "folder/two.txt");
        }
        Assert.Equal("ZIPに圧縮「archive.zip」", history.UndoDescription);

        history.Undo();
        Assert.False(File.Exists(zipPath));
        Assert.True(history.CanRedo);

        history.Redo();
        Assert.True(File.Exists(zipPath));
        using var recreated = ZipFile.OpenRead(zipPath);
        Assert.Contains(recreated.Entries, e => e.FullName == "one.txt");
    }

    [Fact]
    public async Task 親フォルダーと子を同時選択しても重複せず生成中ZIPを取り込まない()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;
        var child = Path.Combine(folder, "child.txt");
        File.WriteAllText(child, "child");
        var workspace = new WorkspaceService(new SafetySettings());
        workspace.OpenFolder(_root);
        var commands = new FolderTreeCommandHandler(workspace, new FileOperationHistory());

        // 子を先に渡す順序でも、親だけをアーカイブ対象にする。
        var zipPath = await commands.CompressToZipAsync([child, folder]);

        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Single(archive.Entries, entry => entry.FullName == "folder/child.txt");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("loomo-tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task キャンセル時は最終ZIPと一時ファイルを残さない()
    {
        var source = Path.Combine(_root, "cancel.txt");
        File.WriteAllText(source, new string('x', 1024));
        var workspace = new WorkspaceService(new SafetySettings());
        workspace.OpenFolder(_root);
        var commands = new FolderTreeCommandHandler(workspace, new FileOperationHistory());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            commands.CompressToZipAsync([source], cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_root, "cancel.zip")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.loomo-tmp-*"));
    }
}
