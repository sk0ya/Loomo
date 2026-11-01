using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>比較基準をリポジトリごとに持つ（§40.9）。</summary>
public sealed class GitCompareByRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-compare-repo", Guid.NewGuid().ToString("N"));

    public GitCompareByRepositoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void リポジトリを切り替えると基準を預けて_戻ると前の基準が出る()
    {
        var a = Repo("a");
        var b = Repo("b");
        var workspace = new WorkspaceService(new SafetySettings());
        workspace.OpenFolder(a);
        workspace.AddFolder(b);
        var git = new GitService(workspace);
        var vm = new GitCompareBaseViewModel(git);
        vm.Restore(new GitCompareSnapshot { Kind = (int)GitCompareBaseKind.Revision, Revision = "abc123" });

        git.SetActiveRoot(b);
        Assert.Equal(GitCompareBaseKind.WorkingTree, vm.Selection.Kind); // 初めて見るリポジトリは作業ツリー基準

        git.SetActiveRoot(a);
        Assert.Equal(GitCompareBaseKind.Revision, vm.Selection.Kind);
        Assert.Equal("abc123", vm.Revision);

        var saved = vm.CaptureAll();
        Assert.NotNull(saved);
        Assert.True(saved!.ContainsKey(a));
        Assert.False(saved.ContainsKey(b)); // 作業ツリー基準は覚えない
    }

    [Fact]
    public void 復元は今のリポジトリの分を使い_無ければ旧い1つの保存を読む()
    {
        var a = Repo("a");
        var workspace = new WorkspaceService(new SafetySettings());
        workspace.OpenFolder(a);
        var git = new GitService(workspace);
        var vm = new GitCompareBaseViewModel(git);

        vm.RestoreAll(null, new GitCompareSnapshot { Kind = (int)GitCompareBaseKind.Revision, Revision = "old" });
        Assert.Equal("old", vm.Revision);

        vm.RestoreAll(new Dictionary<string, GitCompareSnapshot>
        {
            [a] = new() { Kind = (int)GitCompareBaseKind.Revision, Revision = "new" },
        }, new GitCompareSnapshot { Kind = (int)GitCompareBaseKind.Revision, Revision = "old" });
        Assert.Equal("new", vm.Revision);
    }

    private string Repo(string name)
    {
        var full = Path.Combine(_root, name);
        Directory.CreateDirectory(full);
        var info = new ProcessStartInfo("git") { WorkingDirectory = full, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add("init");
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return full;
    }
}
