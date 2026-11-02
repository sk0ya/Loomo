using System.IO;
using System.Linq;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 中身がすべて未追跡のフォルダーを、フォルダー1行（<c>dir/</c>）に畳まずファイルごとに返す。
/// 畳むと Git パネルの「バージョン管理外」に名前の無いフォルダー行だけが出て、中のファイルが見えなかった。
/// </summary>
public sealed class GitUntrackedStatusTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-untracked-tests", Guid.NewGuid().ToString("N"));
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        _git = new GitService(workspace);
        Assert.True((await _git.RunAsync("init")).Success);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* Git がファイルを解放するまでの競合は無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task 未追跡フォルダーの中身をファイルごとに返す()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".harness", "runs"));
        File.WriteAllText(Path.Combine(_root, ".harness", "runs", "a.json"), "{}");
        File.WriteAllText(Path.Combine(_root, ".harness", "b.txt"), "b");

        var status = await _git.GetStatusAsync();

        var untracked = status.Unstaged.Where(e => e.IsUntracked).Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal([".harness/b.txt", ".harness/runs/a.json"], untracked);
    }
}
