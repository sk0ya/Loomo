using System.IO;
using System.Text;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// スタッシュの中身を見る・パッチとして保存する（§24.31）。実際の git で一時リポジトリを作って回す。
/// 差分の範囲（親 → スタッシュ）に退避した変更が出ること、未追跡のファイルは範囲に出ない代わりに
/// 件数で分かり、パッチには入ること、保存したパッチがそのまま当たることを確かめる。
/// </summary>
public sealed class GitStashContentTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-stash", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

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
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "one\n");
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", "first");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 親からスタッシュの範囲に退避した変更が出る()
    {
        var head = (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "two\n");
        await MustRunAsync("stash", "push", "-m", "途中");

        var snapshot = await _git.ResolveStashAsync("stash@{0}");

        Assert.NotNull(snapshot);
        Assert.Equal(head, snapshot!.BaseHash);
        Assert.Equal(0, snapshot.UntrackedCount);
        var diff = await MustRunAsync("diff", snapshot.BaseHash, snapshot.StashHash);
        Assert.Contains("+two", diff.Output);
    }

    [Fact]
    public async Task 未追跡のファイルは件数で分かる()
    {
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "two\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "new1.txt"), "n1\n");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "sub", "new2.txt"), "n2\n");
        await MustRunAsync("stash", "push", "-u");

        var snapshot = await _git.ResolveStashAsync("stash@{0}");

        Assert.Equal(2, snapshot!.UntrackedCount);
    }

    [Fact]
    public async Task 無いスタッシュはnull()
    {
        Assert.Null(await _git.ResolveStashAsync("stash@{3}"));
    }

    [Fact]
    public async Task 保存したパッチは未追跡のファイルとUTF8でない中身も含めてそのまま当たる()
    {
        await File.AppendAllTextAsync(Path.Combine(_root, "a.txt"), "two\n");
        var sjis = CodePagesEncoding().GetBytes("シフトJIS の行\n");
        await File.WriteAllBytesAsync(Path.Combine(_root, "sjis.txt"), sjis);
        await File.WriteAllBytesAsync(Path.Combine(_root, "blob.bin"), new byte[] { 0, 1, 2, 255, 0 });
        await MustRunAsync("stash", "push", "-u");
        var patch = Path.Combine(Path.GetTempPath(), $"loomo-stash-{Guid.NewGuid():N}.patch");
        try
        {
            var result = await _git.SaveStashPatchAsync("stash@{0}", patch);
            Assert.True(result.Success, result.Message);

            // 退避したので作業ツリーは元のまま。パッチを当てると退避した状態に戻る。
            await MustRunAsync("apply", patch);
            Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(Path.Combine(_root, "a.txt")));
            Assert.Equal(sjis, await File.ReadAllBytesAsync(Path.Combine(_root, "sjis.txt")));
            Assert.Equal(new byte[] { 0, 1, 2, 255, 0 },
                await File.ReadAllBytesAsync(Path.Combine(_root, "blob.bin")));
        }
        finally
        {
            File.Delete(patch);
        }
    }

    [Theory]
    [InlineData("stash@{0}", "On main: 作業中", "stash-0-On-main-作業中.patch")]
    [InlineData("stash@{12}", "WIP on feature/x: abc1234 直す", "stash-12-WIP-on-featurex-abc1234-直す.patch")]
    [InlineData("stash@{1}", "", "stash-1.patch")]
    public void パッチの既定のファイル名(string stashRef, string description, string expected)
    {
        Assert.Equal(expected, new GitStashEntry(stashRef, description).PatchFileName);
    }

    [Fact]
    public void 長い説明は詰める()
    {
        var name = new GitStashEntry("stash@{0}", new string('あ', 100)).PatchFileName;
        Assert.Equal("stash-0-" + new string('あ', 40) + ".patch", name);
    }

    private static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
