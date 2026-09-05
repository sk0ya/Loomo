using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>読み取りキャッシュが<b>失敗を覚えない</b>ことの検証。
/// 一度の git 失敗を「リポジトリではない」として握ると、更新しても同じ嘘が返り続け、
/// Git パネルが空のまま復帰しなくなる（rebase 中の一時的な失敗で実際に起きる）。</summary>
public sealed class GitServiceReadCacheTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-cache-tests", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        var init = await _git.RunAsync("init");
        Assert.True(init.Success, init.Error);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 一時的なgit失敗はキャッシュに残らず次の照会で復帰する()
    {
        var config = Path.Combine(_root, ".git", "config");
        var original = await File.ReadAllTextAsync(config);
        // git が失敗する状態を作る（設定ファイルの構文を壊す）。.git は在るので「リポジトリでない」ではない。
        await File.WriteAllTextAsync(config, "[core\n");

        var broken = await _git.GetStatusAsync();

        Assert.False(broken.IsRepository);
        Assert.True(broken.QueryFailed, "git の失敗が『リポジトリではない』と区別されていない");

        await File.WriteAllTextAsync(config, original);

        // 破棄を挟まずに引き直す——ここでキャッシュを引くと、直った後も空のままになる。
        var recovered = await _git.GetStatusAsync();

        Assert.True(recovered.IsRepository);
        Assert.False(recovered.QueryFailed);
    }

    [Fact]
    public async Task リポジトリでない場所の答えは安定しているのでキャッシュしてよい()
    {
        var outside = Path.Combine(Path.GetTempPath(), "loomo-git-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var workspace = new FakeWorkspaceService();
            workspace.OpenFolder(outside);
            var git = new GitService(workspace);

            var status = await git.GetStatusAsync();

            Assert.False(status.IsRepository);
            Assert.False(status.QueryFailed);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* 後始末の失敗は無視 */ }
        }
    }
}
