using System.IO;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>bisect を Git ペインで（§24.100）。</summary>
public sealed class GitBisectParserTests
{
    [Fact]
    public void 残りの数と試している版を読む()
    {
        var step = GitBisectParser.Parse(
            "Bisecting: 5 revisions left to test after this (roughly 3 steps)\n[0123456789abcdef0123456789abcdef01234567] Add parser\n");
        Assert.Equal(5, step.Remaining);
        Assert.Equal(3, step.Steps);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", step.CurrentHash);
        Assert.Equal("Add parser", step.CurrentSubject);
        Assert.False(step.IsFinished);

        var one = GitBisectParser.Parse("Bisecting: 0 revisions left to test after this (roughly 0 steps)\n[abcdef1] x\n");
        Assert.Equal(0, one.Remaining);
    }

    [Fact]
    public void 見つかった犯人と_絞れなかった候補を読む()
    {
        var found = GitBisectParser.Parse("abcdef0123456789abcdef0123456789abcdef01 is the first bad commit\ncommit abcdef01…\n");
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", found.Found);
        Assert.True(found.IsFinished);

        var ambiguous = GitBisectParser.Parse(
            "There are only 'skip'ped commits left to test.\nThe first bad commit could be any of:\n1111111111111111111111111111111111111111\n2222222222222222222222222222222222222222\nWe cannot bisect more!\n");
        Assert.Equal(2, ambiguous.Candidates.Count);
        Assert.True(ambiguous.IsFinished);

        Assert.False(GitBisectParser.Parse("something else").IsFinished);
    }

    [Fact]
    public void 終了コードの決まりはbisect_runと同じ()
    {
        Assert.Equal(GitBisectVerdict.Good, GitBisectParser.VerdictFromExitCode(0));
        Assert.Equal(GitBisectVerdict.Skip, GitBisectParser.VerdictFromExitCode(125));
        Assert.Equal(GitBisectVerdict.Bad, GitBisectParser.VerdictFromExitCode(1));
        Assert.Null(GitBisectParser.VerdictFromExitCode(null));
    }

    [Fact]
    public void 帯の文面()
    {
        Assert.Equal("", BisectPresentation.Label(false, null));
        Assert.StartsWith("bisect 中です", BisectPresentation.Label(true, null));
        Assert.Equal("bisect 中（あと約 3 歩） · 試している版: 01234567 Add parser",
            BisectPresentation.Label(true, new GitBisectStep { CurrentHash = "0123456789", CurrentSubject = "Add parser", Remaining = 5, Steps = 3 }));
    }
}

/// <summary>実際の git で始めて・判定して・犯人が見つかるまで。</summary>
public sealed class GitBisectServiceTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-bisect", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;
    private readonly List<string> _commits = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        await MustRunAsync("init");
        await MustRunAsync("symbolic-ref", "HEAD", "refs/heads/main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
        await MustRunAsync("config", "core.autocrlf", "false");
        // c0..c3 は良い、c4（壊した）以降は悪い。
        for (var i = 0; i < 8; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "value.txt"), i < 4 ? "ok\n" : "broken\n");
            await File.WriteAllTextAsync(Path.Combine(_root, "n.txt"), $"{i}\n");
            await MustRunAsync("add", "-A");
            await MustRunAsync("commit", "-q", "-m", $"c{i}");
            _commits.Add((await MustRunAsync("rev-parse", "HEAD")).Output.Trim());
        }
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 良い悪いを付けていくと最初に悪くなったコミットが見つかる()
    {
        var (start, step) = await _git.BisectStartAsync("HEAD", _commits[0]);
        Assert.True(start.Success, start.Message);
        Assert.True(await _git.IsBisectingAsync());
        Assert.NotNull(step.CurrentHash);

        for (var guard = 0; guard < 10 && !step.IsFinished; guard++)
        {
            var broken = (await File.ReadAllTextAsync(Path.Combine(_root, "value.txt"))).StartsWith("broken");
            (_, step) = await _git.BisectMarkAsync(broken ? GitBisectVerdict.Bad : GitBisectVerdict.Good);
        }

        Assert.Equal(_commits[4], step.Found);
        Assert.True((await _git.BisectResetAsync()).Success);
        Assert.False(await _git.IsBisectingAsync());
        Assert.Equal("main", (await MustRunAsync("branch", "--show-current")).Output.Trim());
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
