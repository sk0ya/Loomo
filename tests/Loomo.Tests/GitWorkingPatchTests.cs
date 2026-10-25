using System.IO;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>作業ツリーの変更をパッチにする（§24.79）。未追跡・削除を含み、人のステージには触らない。</summary>
public sealed class GitWorkingPatchTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-working-patch", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;
    private FakeWorkspaceService _workspace = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_root);
        _git = new GitService(_workspace);
        await MustRunAsync("init");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
        await MustRunAsync("config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "gone.txt"), "gone\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "keep.txt"), "keep\n");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "first");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 未追跡と削除を含み_選んだパスだけを入れる()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a2\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "new\n");
        File.Delete(Path.Combine(_root, "gone.txt"));
        await File.WriteAllTextAsync(Path.Combine(_root, "keep.txt"), "changed but not picked\n");

        var (text, result) = await _git.GetWorkingPatchTextAsync(["a.txt", "new.txt", "gone.txt"]);

        Assert.True(result.Success, result.Message);
        Assert.Contains("+a2", text);
        Assert.Contains("new file mode", text);
        Assert.Contains("+new", text);
        Assert.Contains("deleted file mode", text);
        Assert.DoesNotContain("keep.txt", text);
    }

    [Fact]
    public async Task 人のステージには触らない()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "new\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a2\n");

        var (text, _) = await _git.GetWorkingPatchTextAsync(["a.txt", "new.txt"]);

        Assert.Contains("new.txt", text);
        var status = await MustRunAsync("status", "--porcelain");
        Assert.Contains("?? new.txt", status.Output);
        Assert.Contains(" M a.txt", status.Output);
    }

    [Fact]
    public async Task 書いたパッチは元に戻した作業ツリーへそのまま当たる()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a2\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "new\n");
        var patch = Path.Combine(_root, "..", Guid.NewGuid().ToString("N") + ".patch");
        try
        {
            var written = await _git.WriteWorkingPatchAsync(["a.txt", "new.txt"], patch);
            Assert.True(written.Success, written.Message);
            await MustRunAsync("checkout", "--", "a.txt");
            File.Delete(Path.Combine(_root, "new.txt"));

            var preview = await _git.PreviewPatchFileAsync(patch);
            Assert.Equal(GitPatchApplyMode.Direct, preview.Mode);
            var applied = await _git.ApplyPatchAsync(preview);

            Assert.True(applied.Success, applied.Message);
            Assert.Equal("a2\n", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
            Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(_root, "new.txt")));
        }
        finally
        {
            File.Delete(patch);
        }
    }

    [Fact]
    public async Task 差が無ければ空()
    {
        var (text, result) = await _git.GetWorkingPatchTextAsync(["a.txt"]);

        Assert.True(result.Success, result.Message);
        Assert.Equal("", text);
    }

    [Fact]
    public async Task 右クリックした行がチェック済みならチェックした全部_そうでなければその行だけ()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a2\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "keep.txt"), "k2\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "new\n");
        var vm = new GitPanelViewModel(_git, new FakeEditorService(), _workspace,
            new GitRootSwitchViewModel(_git, _workspace), new GitCompareBaseViewModel(_git));
        await vm.RefreshCommand.ExecuteAsync(null);
        var a = Assert.Single(vm.Changes, c => c.Entry.Path == "a.txt");
        var keep = Assert.Single(vm.Changes, c => c.Entry.Path == "keep.txt");
        var created = Assert.Single(vm.UnversionedFiles);
        a.IsChecked = true;
        created.IsChecked = true;

        Assert.Equal(["a.txt", "new.txt"], vm.PathsForChangesAction(a).Order());
        Assert.Equal(["keep.txt"], vm.PathsForChangesAction(keep));
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
