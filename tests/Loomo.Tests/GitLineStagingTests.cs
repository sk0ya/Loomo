using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.Core.Diff;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// HEAD↔作業ツリーの差分（人が見る1枚）の行番号で選んだ変更を、<see cref="StagedChangeMap"/> で振り分けて
/// ステージ／アンステージする流れを実リポジトリで確かめる。縮約パッチは見出しの行数が本文と合わないので、
/// <c>git apply --cached --recount</c> が本当に受け付けて、選んだ行だけがインデックスへ入る／出る
/// （作業ツリーは変わらない）ことまで見ないと意味がない。
/// </summary>
public sealed class GitLineStagingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-line-staging-tests", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

    private const string Original = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\n";
    // 2ハンクに分かれる変更：先頭近く（b→B、c の後に c2 を追加）と末尾近く（k を削除）
    // HEAD↔作業ツリーの行番号：-b は旧2、+B は新2、+c2 は新4、-k は旧11
    private const string Modified = "a\nB\nc\nc2\nd\ne\nf\ng\nh\ni\nj\nl\n";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        await MustRunAsync("init");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
        await MustRunAsync("config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(FilePath, Original);
        await MustRunAsync("add", "f.txt");
        await MustRunAsync("commit", "-m", "base");
        await File.WriteAllTextAsync(FilePath, Modified);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* Git がファイルを解放するまでの競合は無視 */ }
        return Task.CompletedTask;
    }

    private string FilePath => Path.Combine(_root, "f.txt");

    [Fact]
    public async Task 選んだ追加行だけがステージされ作業ツリーは変わらない()
    {
        await StageAsync(head: [], worktree: [4], stage: true);

        Assert.Equal("a\nb\nc\nc2\nd\ne\nf\ng\nh\ni\nj\nk\nl\n", await IndexContentAsync());
        Assert.Equal(Modified, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task 後ろのハンクの削除行だけをステージできる()
    {
        await StageAsync(head: [11], worktree: [], stage: true);

        Assert.Equal("a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nl\n", await IndexContentAsync());
    }

    [Fact]
    public async Task ステージしても表示の差分は変わらず行の印だけが変わる()
    {
        var before = await HeadDiffAsync();
        await StageAsync(head: [2], worktree: [2], stage: true);

        Assert.Equal(before, await HeadDiffAsync());   // ステージで差分から行が消えない
        var map = await MapAsync();
        Assert.True(map.IsStagedRemoval(2));
        Assert.True(map.IsStagedAddition(2));
        Assert.False(map.IsStagedAddition(4));
        Assert.False(map.IsStagedRemoval(11));
    }

    [Fact]
    public async Task ステージ済みの一部だけをアンステージできる()
    {
        await MustRunAsync("add", "f.txt");
        Assert.True((await MapAsync()).IsStagedAddition(4));

        await StageAsync(head: [2], worktree: [2], stage: false);

        Assert.Equal("a\nb\nc\nc2\nd\ne\nf\ng\nh\ni\nj\nl\n", await IndexContentAsync());
        var map = await MapAsync();
        Assert.False(map.IsStagedRemoval(2));
        Assert.False(map.IsStagedAddition(2));
        Assert.True(map.IsStagedAddition(4));
        Assert.True(map.IsStagedRemoval(11));
        Assert.Equal(Modified, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task 一部ステージした後に残りをステージすると全部入る()
    {
        await StageAsync(head: [11], worktree: [], stage: true);
        // 後ろのハンクだけインデックスに入った状態で、前のハンクを HEAD の行番号のまま選んでステージする。
        await StageAsync(head: [2], worktree: [2, 4], stage: true);

        Assert.Equal(Modified, await IndexContentAsync());
    }

    private async Task StageAsync(int[] head, int[] worktree, bool stage)
    {
        var entry = await EntryAsync();
        var stagedPatch = await _git.GetDiffTextAsync(entry, staged: true, 3);
        var unstagedPatch = await _git.GetDiffTextAsync(entry, staged: false, 3);
        var split = StagedChangeMap.Build(stagedPatch, unstagedPatch).Split(head, worktree);
        var reduced = stage
            ? UnifiedPatchEditor.BuildStagePatchForLines(unstagedPatch, split.Unstaged.OldLines, split.Unstaged.NewLines)
            : UnifiedPatchEditor.BuildReverseDiscardPatchForLines(stagedPatch, split.Staged.OldLines, split.Staged.NewLines);
        Assert.False(reduced.IsEmpty);

        var apply = await _git.ApplyCachedPatchAsync(reduced.Patch, reverse: !stage);
        Assert.True(apply.Success, apply.Message);
    }

    private async Task<StagedChangeMap> MapAsync()
    {
        var entry = await EntryAsync();
        return StagedChangeMap.Build(
            await _git.GetDiffTextAsync(entry, staged: true, 3),
            await _git.GetDiffTextAsync(entry, staged: false, 3));
    }

    private async Task<string> HeadDiffAsync() => await _git.GetHeadDiffTextAsync(await EntryAsync(), 3);

    private async Task<GitChangeEntry> EntryAsync()
    {
        var status = await _git.GetStatusAsync();
        return status.Unstaged.Concat(status.Staged).First(e => e.Path == "f.txt");
    }

    private async Task<string> IndexContentAsync()
        => (await MustRunAsync("show", ":f.txt")).Output.Replace("\r\n", "\n");

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
