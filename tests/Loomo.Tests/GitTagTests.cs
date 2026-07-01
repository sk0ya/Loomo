using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class GitTagTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-tag-tests", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        await MustRunAsync("init");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* Git がファイルを解放するまでの競合は無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 軽量タグと注釈付きタグを区別して取得できる()
    {
        var first = await CommitAsync("one.txt", "one", "first");
        await CommitAsync("two.txt", "two", "second");

        var lightweightResult = await _git.CreateTagAsync("v1-light", first);
        Assert.True(lightweightResult.Success, lightweightResult.Error);
        var annotatedResult = await _git.CreateTagAsync("v2-annotated", message: "リリースメモ");
        Assert.True(annotatedResult.Success, annotatedResult.Error);

        var tags = await _git.GetTagsAsync();

        var light = Assert.Single(tags, t => t.Name == "v1-light");
        Assert.False(light.IsAnnotated);
        Assert.Equal(first[..light.TargetShortHash.Length], light.TargetShortHash);
        Assert.Equal("first", light.Subject);

        var annotated = Assert.Single(tags, t => t.Name == "v2-annotated");
        Assert.True(annotated.IsAnnotated);
        Assert.Equal("リリースメモ", annotated.Subject);
    }

    [Fact]
    public async Task タグ削除後は一覧から消える()
    {
        await CommitAsync("one.txt", "one", "first");
        await MustRunAsync("tag", "to-delete");

        var result = await _git.DeleteTagAsync("to-delete");

        Assert.True(result.Success, result.Error);
        var tags = await _git.GetTagsAsync();
        Assert.DoesNotContain(tags, t => t.Name == "to-delete");
    }

    [Fact]
    public async Task HEAD以外の指定コミットにタグを作成できる()
    {
        var first = await CommitAsync("one.txt", "one", "first");
        var second = await CommitAsync("two.txt", "two", "second");

        var result = await _git.CreateTagAsync("on-first", first);

        Assert.True(result.Success, result.Error);
        var tags = await _git.GetTagsAsync();
        var tag = Assert.Single(tags, t => t.Name == "on-first");
        Assert.Equal(first[..tag.TargetShortHash.Length], tag.TargetShortHash);
        Assert.NotEqual(second[..tag.TargetShortHash.Length], tag.TargetShortHash);
    }

    private async Task<string> CommitAsync(string path, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, path), content);
        await MustRunAsync("add", path);
        await MustRunAsync("commit", "-m", message);
        return (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
