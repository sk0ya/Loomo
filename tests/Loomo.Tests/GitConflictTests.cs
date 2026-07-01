using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class GitConflictTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-conflict-tests", Guid.NewGuid().ToString("N"));
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
    public async Task 内容衝突で実際のoursとtheirsを取得できる()
    {
        await CreateConflictingBranchesAsync();

        var mergeResult = await _git.MergeAsync("feature");
        Assert.False(mergeResult.Success);

        var status = await _git.GetStatusAsync();
        Assert.Contains(status.Unstaged, e => e.Path == "file.txt" && e.IsConflicted);

        var (baseContent, ours, theirs) = await _git.GetConflictSidesAsync("file.txt");
        Assert.Equal("line1\nline2\nline3\n", baseContent);
        Assert.Equal("line1\nours-change\nline3\n", ours);
        Assert.Equal("line1\ntheirs-change\nline3\n", theirs);
    }

    [Fact]
    public async Task 削除変更衝突では欠けている側がnullになる()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "line1\nline2\n");
        await MustRunAsync("add", "file.txt");
        await MustRunAsync("commit", "-m", "base");

        await MustRunAsync("switch", "-c", "feature");
        await MustRunAsync("rm", "file.txt");
        await MustRunAsync("commit", "-m", "delete-on-feature");

        await MustRunAsync("switch", "-");
        await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "line1\nline2-modified\n");
        await MustRunAsync("add", "file.txt");
        await MustRunAsync("commit", "-m", "modify-on-main");

        var mergeResult = await _git.MergeAsync("feature");
        Assert.False(mergeResult.Success);

        var (baseContent, ours, theirs) = await _git.GetConflictSidesAsync("file.txt");
        Assert.Equal("line1\nline2\n", baseContent);
        Assert.Equal("line1\nline2-modified\n", ours);
        Assert.Null(theirs);
    }

    [Fact]
    public async Task マーカー解決から解決済みまでのエンドツーエンド()
    {
        await CreateConflictingBranchesAsync();
        await _git.MergeAsync("feature");
        Assert.True((await _git.GetStatusAsync()).Unstaged.Single(e => e.Path == "file.txt").IsConflicted);

        var filePath = Path.Combine(_root, "file.txt");
        var raw = await File.ReadAllTextAsync(filePath);
        var parsed = ConflictMarkerParser.Parse(raw);
        Assert.True(parsed.HasConflicts);
        var conflictIndex = parsed.Regions.ToList().FindIndex(r => r.Kind == ConflictRegionKind.Conflict);

        var resolved = ConflictMarkerParser.ResolveRegion(parsed, conflictIndex, ConflictResolution.Ours);
        Assert.False(ConflictMarkerParser.Parse(resolved).HasConflicts);
        await File.WriteAllTextAsync(filePath, resolved);

        var stageResult = await _git.StageAsync("file.txt");
        Assert.True(stageResult.Success, stageResult.Error);

        var status = await _git.GetStatusAsync();
        Assert.DoesNotContain(status.Staged.Concat(status.Unstaged), e => e.IsConflicted);
    }

    /// <summary>base → feature（theirs-change） と base → 現在ブランチ（ours-change）に分岐させる
    /// （file.txt の同じ行をそれぞれ書き換えるので、merge すると内容衝突になる）。</summary>
    private async Task CreateConflictingBranchesAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "line1\nline2\nline3\n");
        await MustRunAsync("add", "file.txt");
        await MustRunAsync("commit", "-m", "base");

        await MustRunAsync("switch", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "line1\ntheirs-change\nline3\n");
        await MustRunAsync("add", "file.txt");
        await MustRunAsync("commit", "-m", "theirs");

        await MustRunAsync("switch", "-");
        await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "line1\nours-change\nline3\n");
        await MustRunAsync("add", "file.txt");
        await MustRunAsync("commit", "-m", "ours");
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
