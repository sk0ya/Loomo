using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>リポジトリの帯（§40.8.3）と、ファイル起点の切替がリポジトリ単位になること（§40.8.4）。</summary>
public sealed class GitRepositoryStripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-repo-strip", Guid.NewGuid().ToString("N"));

    public GitRepositoryStripTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void 帯はフォルダーの順に_リポジトリと管理外のフォルダーと入れ子のリポジトリを並べる()
    {
        var app = Repo("app");
        var work = Dir("work");
        var api = Repo(Path.Combine("work", "api"));
        var web = Repo(Path.Combine("work", "web"));
        var (git, workspace) = NewGit(app, work);
        var strip = new GitRepositoryStripViewModel(git, workspace);

        var items = strip.Build(workspace.Folders, git.Repositories.Repositories);

        Assert.Equal(new[] { app, work, api, web }, items.Select(i => i.Root));
        Assert.True(items[0].IsRepository);
        Assert.False(items[1].IsRepository);
        Assert.Contains("Git 管理外", items[1].Label);
        Assert.Equal("api", items[2].Label);
    }

    [Fact]
    public void 帯で選ぶとGitの対象がそのリポジトリになり_入れ子のリポジトリも選べる()
    {
        var app = Repo("app");
        var work = Dir("work");
        var api = Repo(Path.Combine("work", "api"));
        var (git, workspace) = NewGit(app, work);
        var strip = new GitRepositoryStripViewModel(git, workspace);
        strip.Apply(strip.Build(workspace.Folders, git.Repositories.Repositories));

        strip.SelectedItem = strip.Items.Single(i => i.Root == api);

        Assert.Equal(api, git.RootPath);
        Assert.Equal("", strip.SwitchNotice); // 帯で選んだときは一言を出さない
    }

    [Fact]
    public void ファイル起点の切替は入れ子のリポジトリを対象にする()
    {
        var app = Repo("app");
        var work = Dir("work");
        var api = Repo(Path.Combine("work", "api"));
        var (git, _) = NewGit(app, work);

        git.SetActiveRootForPath(Path.Combine(api, "a.txt"));

        // 以前はワークスペースフォルダー（Git 管理外の work）になって履歴が引けなかった。
        Assert.Equal(api, git.RootPath);

        git.SetActiveRootForPath(Path.Combine(app, "a.txt"));
        Assert.Equal(app, git.RootPath);
    }

    private (GitService Git, WorkspaceService Workspace) NewGit(string primary, params string[] more)
    {
        var workspace = new WorkspaceService(new SafetySettings());
        workspace.OpenFolder(primary);
        foreach (var folder in more) workspace.AddFolder(folder);
        return (new GitService(workspace), workspace);
    }

    private string Dir(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    private string Repo(string relative)
    {
        var full = Dir(relative);
        var info = new ProcessStartInfo("git") { WorkingDirectory = full, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add("init");
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return full;
    }
}
