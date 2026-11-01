using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>エクスプローラーのリポジトリの見せ方（§40.8.1）：見出し行と入れ子のリポジトリの印・境目・「変更のみ」の畳み。</summary>
public sealed class FolderTreeRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-tree-repo", Guid.NewGuid().ToString("N"));

    public FolderTreeRepositoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void 入れ子のリポジトリがあるだけでは外側のリポジトリの変更にならない()
    {
        var outer = Repo("outer", "a.txt");
        Repo(Path.Combine("outer", "vendor", "lib"), "b.txt");

        var tree = GitTreeState.Load(outer, CancellationToken.None);

        Assert.Empty(tree.ChangedFiles);
        Assert.Equal(GitChangeKind.None, tree.GetStatus(Path.Combine(outer, "vendor"), true));
        Assert.Equal(0, tree.Repositories.Single(r => r.Root == outer).ChangedCount);
    }

    [Fact]
    public async Task 見出し行はフォルダーのリポジトリの印を持ち_入れ子のリポジトリの行にも印が付く()
    {
        var app = Repo("app", "a.txt");
        var work = Dir("work");
        var api = Repo(Path.Combine("work", "api"), "i.ts");
        Dir(Path.Combine("work", "notes"));
        var (tree, _) = NewTree(app, work);

        await tree.WhenTreeLoadedAsync();

        var appHeader = tree.Nodes.Single(n => n.FullPath == app);
        var workHeader = tree.Nodes.Single(n => n.FullPath == work);
        Assert.Equal(app, appHeader.Repository?.Root);
        Assert.Null(workHeader.Repository);

        workHeader.IsExpanded = true;
        Assert.Equal(api, workHeader.Children.Single(c => c.FullPath == api).Repository?.Root);
        Assert.Null(workHeader.Children.Single(c => c.Name == "notes").Repository);
        Assert.Null(appHeader.Children.Single(c => c.Name == "a.txt").Repository);
    }

    [Fact]
    public async Task 変更のみでは変更の無いリポジトリの見出しを畳む()
    {
        var dirty = Repo("dirty", "a.txt");
        File.WriteAllText(Path.Combine(dirty, "a.txt"), "changed");
        var clean = Repo("clean", "b.txt");
        var (tree, _) = NewTree(dirty, clean);
        tree.ShowChangedOnly = true;

        await tree.WhenTreeLoadedAsync();

        Assert.True(tree.Nodes.Single(n => n.FullPath == dirty).IsExpanded);
        Assert.False(tree.Nodes.Single(n => n.FullPath == clean).IsExpanded);
    }

    private (FolderTreeViewModel Tree, WorkspaceService Workspace) NewTree(string primary, params string[] more)
    {
        var workspace = new WorkspaceService(new SafetySettings());
        var catalog = new GitRepositoryCatalog(workspace);
        var git = new GitService(workspace, null, catalog);
        var status = new GitWorkspaceStatus(catalog, workspace);
        var chips = new GitRepositoryChips(status, null, action => action());
        var tree = new FolderTreeViewModel(workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(_root, "wf")),
            new FolderTreeCommandHandler(workspace, new FileOperationHistory()),
            new FolderTreeQuery(gitStatus: status), gitService: git, repositoryChips: chips);
        workspace.OpenFolder(primary);
        tree.LoadRoot(primary);
        foreach (var folder in more)
            workspace.AddFolder(folder);
        return (tree, workspace);
    }

    private string Dir(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    private string Repo(string relative, string file)
    {
        var full = Dir(relative);
        Git(full, "init");
        Git(full, "config", "user.email", "loomo@example.test");
        Git(full, "config", "user.name", "Loomo Tests");
        File.WriteAllText(Path.Combine(full, file), "original");
        Git(full, "add", file);
        Git(full, "commit", "-m", "initial");
        return full;
    }

    private static void Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
    }
}
