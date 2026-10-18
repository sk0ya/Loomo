using System.IO;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>外から来たパッチを作業ツリーへ当てる（§24.38.1）。実 git。</summary>
public sealed class GitPatchApplyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-patch-apply-tests", Guid.NewGuid().ToString("N"));
    private FakeWorkspaceService _workspace = null!;
    private GitService _git = null!;

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
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), Lines("3"));
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "init");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    /// <summary>1〜10 行。<paramref name="third"/> は 3 行目、<paramref name="sixth"/> は 6 行目。</summary>
    private static string Lines(string third, string sixth = "6", string fourth = "4")
        => $"1\n2\n{third}\n{fourth}\n5\n{sixth}\n7\n8\n9\n10\n";

    /// <summary>a.txt の 3 行目を変えたパッチを作り、作業ツリーを元に戻して返す。</summary>
    private async Task<string> MakePatchAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), Lines("three"));
        var diff = (await MustRunAsync("diff")).Output;
        await MustRunAsync("checkout", "--", "a.txt");
        return diff;
    }

    [Fact]
    public async Task そのまま当たるパッチは作業ツリーだけに当たる()
    {
        var patch = await MakePatchAsync();

        var preview = await _git.PreviewPatchTextAsync(patch);
        Assert.Equal(GitPatchApplyMode.Direct, preview.Mode);
        Assert.Equal(new GitLineStat(1, 1, false), preview.Files["a.txt"]);

        var result = await _git.ApplyPatchAsync(preview);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Lines("three"), await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
        // ステージは触らない。
        Assert.Equal("", (await MustRunAsync("diff", "--cached", "--name-only")).Output.Trim());
    }

    [Fact]
    public async Task クリップボードで改行がCRLFになったパッチも当たる()
    {
        var patch = (await MakePatchAsync()).Replace("\n", "\r\n");

        var preview = await _git.PreviewPatchTextAsync(patch);

        Assert.Equal(GitPatchApplyMode.Direct, preview.Mode);
        Assert.True((await _git.ApplyPatchAsync(preview)).Success);
        Assert.Equal(Lines("three"), await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task 周りが変わっていてそのままでは当たらなければ3wayで当てる()
    {
        var patch = await MakePatchAsync();
        // パッチの文脈の端（6 行目）を変えてコミットしておく。そのままでは当たらないが、ベースのオブジェクトがあり
        // 変えた場所が離れているので 3-way なら衝突せずに当たる。
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), Lines("3", sixth: "SIX"));
        await MustRunAsync("commit", "-am", "文脈を変える");

        var preview = await _git.PreviewPatchTextAsync(patch);
        Assert.Equal(GitPatchApplyMode.ThreeWay, preview.Mode);

        var result = await _git.ApplyPatchAsync(preview);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Lines("three", sixth: "SIX"), await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task ThreeWayで衝突したら失敗ではなく直す番として知らせる()
    {
        var patch = await MakePatchAsync();
        // 隣の行（4 行目）を変える＝3-way でも衝突する。
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), Lines("3", fourth: "FOUR"));
        await MustRunAsync("commit", "-am", "隣を変える");
        var clip = Path.Combine(_root, "x.patch");
        await File.WriteAllTextAsync(clip, patch);
        var vm = new GitPanelViewModel(_git, new FakeEditorService(), _workspace,
            new GitRootSwitchViewModel(_git, _workspace), new GitCompareBaseViewModel(_git));

        await vm.ApplyPatchFileAsync(clip);

        Assert.Contains("衝突したファイルがあります", vm.StatusMessage);
        Assert.Contains("<<<<<<<", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task パッチでない文字は当てない()
    {
        var preview = await _git.PreviewPatchTextAsync("これはただの文章です。");

        Assert.Equal(GitPatchApplyMode.None, preview.Mode);
        Assert.NotNull(preview.Error);
        Assert.False((await _git.ApplyPatchAsync(preview)).Success);
    }

    [Fact]
    public void 確認の文言にファイルと増減と方式が出る()
    {
        var preview = new GitPatchPreview(
            new Dictionary<string, GitLineStat> { ["src/a.cs"] = new(3, 1, false), ["img.png"] = new(0, 0, true) },
            GitPatchApplyMode.ThreeWay, null, "x.patch");

        var text = GitPanelViewModel.DescribePatch("x.patch", preview);

        Assert.Contains("src/a.cs  +3 −1", text);
        Assert.Contains("img.png  bin", text);
        Assert.Contains("3-way", text);
        Assert.Contains("2 ファイル", text);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
