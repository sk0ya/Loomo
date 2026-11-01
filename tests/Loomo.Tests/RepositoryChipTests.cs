using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>リポジトリの印（§40.7）：文言と、リポジトリごとに1つの印。</summary>
public sealed class RepositoryChipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-repo-chip", Guid.NewGuid().ToString("N"));

    public RepositoryChipTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void 変更のないリポジトリはブランチだけで_数は出さない()
    {
        var repo = Repo("clean");
        var text = RepositoryChipViewModel.Describe(GitRepositoryFileState.Load(repo, CancellationToken.None));

        Assert.False(string.IsNullOrEmpty(text.Branch));
        Assert.Equal("", text.Ahead);
        Assert.Equal("", text.Behind);
        Assert.Equal("", text.Changes);
        Assert.Equal("", text.Conflicts);
        Assert.Equal("", text.Operation);
        Assert.Contains("上流なし", text.ToolTip);
    }

    [Fact]
    public void 変更と止まっている操作を出す()
    {
        var repo = Repo("dirty");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "changed");
        File.WriteAllText(Path.Combine(repo, ".git", "MERGE_HEAD"), "0000000000000000000000000000000000000000\n");

        var text = RepositoryChipViewModel.Describe(GitRepositoryFileState.Load(repo, CancellationToken.None));

        Assert.Equal("±1", text.Changes);
        Assert.Equal("MERGING", text.Operation);
        Assert.Contains("変更 1 件", text.ToolTip);
    }

    [Fact]
    public void 同じリポジトリには同じ印を返し_根でない場所には印を付けない()
    {
        var work = Path.Combine(_root, "work");
        var api = Repo(Path.Combine("work", "api"));
        var catalog = new GitRepositoryCatalog(new FakeWorkspaceService(work));
        using var status = new GitWorkspaceStatus(catalog, null, GitRepositoryFileState.Load);
        var chips = new GitRepositoryChips(status, null, action => action());

        var chip = chips.For(api);
        Assert.Same(chip, chips.For(api + Path.DirectorySeparatorChar));
        Assert.Same(chip, chips.ForRootPath(api));
        Assert.Null(chips.ForRootPath(Path.Combine(api, "a.txt")));
        Assert.Null(chips.ForRootPath(work));

        status.Load(api, fresh: true, CancellationToken.None);
        Assert.True(chip.IsLoaded);
        Assert.Equal("api", chip.Name);
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
