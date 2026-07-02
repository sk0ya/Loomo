using System.Diagnostics;
using System.IO;
using System.Linq;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// <see cref="FolderTreeViewModel.AddToGitignore"/>（右クリック「.gitignore に追加」）の検証。
/// </summary>
public sealed class FolderTreeGitignoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-gitignore-{Guid.NewGuid():N}");

    public FolderTreeGitignoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 一時フォルダの削除失敗は無視 */ }
    }

    private static void RunGit(string root, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        process.WaitForExit();
    }

    private FolderTreeViewModel CreateSut()
        => new(new FakeWorkspaceService(), new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(Path.GetTempPath(), "loomo-test-workflows")));

    [Fact]
    public async Task ファイルを追加すると相対パスが1行追記される()
    {
        RunGit(_root, "init");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "");

        var sut = CreateSut();
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        var node = sut.Nodes.Single(n => n.Name == "secret.txt");
        sut.AddToGitignore(node);

        var gitignorePath = Path.Combine(_root, ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        Assert.Equal(new[] { "secret.txt" }, File.ReadAllLines(gitignorePath));
    }

    [Fact]
    public async Task フォルダを追加すると末尾にスラッシュが付く()
    {
        RunGit(_root, "init");
        Directory.CreateDirectory(Path.Combine(_root, "bin"));

        var sut = CreateSut();
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        var node = sut.Nodes.Single(n => n.Name == "bin");
        sut.AddToGitignore(node);

        var lines = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Equal(new[] { "bin/" }, lines);
    }

    [Fact]
    public async Task 既存の行と同じなら重複追加しない()
    {
        RunGit(_root, "init");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "secret.txt\n");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "");

        var sut = CreateSut();
        sut.HideIgnoredFiles = false;   // 既に .gitignore 対象＝既定では非表示になるノードを拾うため
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        var node = sut.Nodes.Single(n => n.Name == "secret.txt");
        sut.AddToGitignore(node);

        var lines = File.ReadAllLines(Path.Combine(_root, ".gitignore"));
        Assert.Equal(new[] { "secret.txt" }, lines);
    }

    [Fact]
    public async Task Gitリポジトリでなければ何もしない()
    {
        // git init しない＝リポジトリではないワークスペース。
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "");

        var sut = CreateSut();
        sut.LoadRoot(_root);
        await sut.WhenTreeLoadedAsync();

        var node = sut.Nodes.Single(n => n.Name == "secret.txt");
        sut.AddToGitignore(node);

        Assert.False(File.Exists(Path.Combine(_root, ".gitignore")));
    }
}
