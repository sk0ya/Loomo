using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>差分ペインの作業ツリーの変更を全リポジトリで1つの一覧に（§40.8.5）。</summary>
public sealed class DiffAcrossRepositoriesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-diff-repos", Guid.NewGuid().ToString("N"));

    public DiffAcrossRepositoriesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task 全リポジトリの変更をリポジトリ名付きで並べ_操作はその項目のリポジトリへ回る()
    {
        var app = Repo("app");
        var work = Path.Combine(_root, "work");
        var api = Repo(Path.Combine("work", "api"));
        File.WriteAllText(Path.Combine(app, "a.txt"), "changed");
        File.WriteAllText(Path.Combine(api, "a.txt"), "changed");
        var workspace = new WorkspaceService(new SafetySettings());
        workspace.OpenFolder(app);
        workspace.AddFolder(work);
        var git = new GitService(workspace);

        var list = await new DiffSessionQuery(git).LoadAsync(null);

        Assert.Equal(new[] { "api/a.txt", "app/a.txt" }, list.Items.Select(i => i.DisplayPath).OrderBy(p => p));
        var apiItem = list.Items.Single(i => i.DisplayPath == "api/a.txt");
        Assert.Equal(api, apiItem.RepositoryRoot);
        Assert.Equal(Path.Combine(api, "a.txt"), apiItem.FullPath);

        // 選んでいるリポジトリ（app）の項目はワークスペースの窓口、api の項目は api に固定した窓口。
        Assert.Same(git, git.ForItem(list.Items.Single(i => i.DisplayPath == "app/a.txt")));
        var apiGit = git.ForItem(apiItem);
        Assert.Equal(api, apiGit.RootPath);

        var diff = await apiGit.GetHeadDiffTextAsync(apiItem.Entry!, 3);
        Assert.Contains("+changed", diff);

        // 固定の窓口で起きた変更もワークスペースの窓口の購読者へ届く（差分ペインが読み直せるように）。
        var notified = 0;
        git.RepositoryChanged += (_, _) => notified++;
        Assert.True((await apiGit.StageAsync(apiItem.Entry!.Path)).Success);
        Assert.True(notified > 0);
    }

    [Fact]
    public async Task コミット範囲は開いたときのリポジトリで引き続ける_選んでいるリポジトリが替わっても空にならない()
    {
        var app = Repo("app");
        var work = Path.Combine(_root, "work");
        var api = Repo(Path.Combine("work", "api"));
        File.WriteAllText(Path.Combine(api, "b.txt"), "api only");
        Git(api, "add", "b.txt");
        Git(api, "commit", "-m", "api commit");
        var hash = Git(api, "rev-parse", "HEAD").Trim();
        var workspace = new WorkspaceService(new SafetySettings());
        workspace.OpenFolder(app);
        workspace.AddFolder(work);
        var git = new GitService(workspace);
        Assert.Equal(app, DiffRepositoryRouting.SelectedRepository(git)); // 選んでいるのは app

        var list = await new DiffSessionQuery(git).LoadAsync((null, hash), null, api);

        var item = Assert.Single(list.Items);
        Assert.Equal("b.txt", item.DisplayPath);
        Assert.Equal(api, item.RepositoryRoot);
        Assert.Equal(Path.Combine(api, "b.txt"), item.FullPath);
        var diff = await git.ForItem(item).GetRangeFileDiffAsync(null, hash, item.CommitFile!, 3);
        Assert.Contains("+api only", diff);
    }

    private string Repo(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(full);
        Git(full, "init");
        Git(full, "config", "user.email", "loomo@example.test");
        Git(full, "config", "user.name", "Loomo Tests");
        File.WriteAllText(Path.Combine(full, "a.txt"), "original");
        Git(full, "add", "a.txt");
        Git(full, "commit", "-m", "initial");
        return full;
    }

    private static string Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
        return output;
    }
}
