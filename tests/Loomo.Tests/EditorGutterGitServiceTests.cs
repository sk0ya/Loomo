using System.IO;
using Editor.Controls.Git;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// エディタのガターを共有の比較基準に合わせる（§24.11.1）。解析は純粋に、基準との差分は実 git で確かめる。
/// </summary>
public sealed class EditorGutterGitServiceTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-gutter", Guid.NewGuid().ToString("N"));
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
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    // ===== 解析 =====

    [Fact]
    public void 追加_変更_削除を新しい側の0始まり行で読む()
    {
        var diff = string.Join('\n',
            "diff --git a/a.txt b/a.txt",
            "--- a/a.txt",
            "+++ b/a.txt",
            "@@ -1,4 +1,4 @@",
            " one",
            "-two",
            "+TWO",
            " three",
            "-four",
            "+five",
            "+six",
            "@@ -10,2 +10,1 @@",
            " ten",
            "-eleven",
            "");

        var lines = GitGutterDiffParser.Parse(diff);

        Assert.Equal(GitLineState.Modified, lines[1]);
        Assert.Equal(GitLineState.Modified, lines[3]);
        Assert.Equal(GitLineState.Added, lines[4]);
        Assert.Equal(GitLineState.Deleted, lines[10]);
        Assert.False(lines.ContainsKey(0));
    }

    [Fact]
    public void CRLFの出力でも同じに読む()
    {
        var lines = GitGutterDiffParser.Parse("@@ -1,1 +1,2 @@\r\n one\r\n+two\r\n");

        Assert.Equal(GitLineState.Added, lines[1]);
        Assert.Single(lines);
    }

    [Fact]
    public void 空の差分は何も印を付けない() => Assert.Empty(GitGutterDiffParser.Parse(""));

    [Theory]
    [InlineData(@"C:\work\app", @"C:\work\app\src\a.cs", true)]
    [InlineData(@"C:\work\app\", @"C:\work\app\a.cs", true)]
    [InlineData(@"C:\work\app", @"C:\work\app2\a.cs", false)]
    [InlineData(@"C:\work\app", @"D:\other\a.cs", false)]
    public void リポジトリの中かは区切り込みで判定する(string folder, string file, bool inside) =>
        Assert.Equal(inside, EditorGutterGitService.IsInside(folder, file));

    // ===== 基準との差分（実 git） =====

    [Fact]
    public async Task 分岐点が基準ならコミット済みの変更もガターに出る()
    {
        await CommitAsync("a.txt", "one\ntwo\n", "base");
        await MustRunAsync("checkout", "-b", "feature");
        await CommitAsync("a.txt", "one\ntwo\nthree\n", "feature work");
        var file = Path.Combine(_root, "a.txt");

        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.MergeBase, "main"));
        var gutterBase = new EditorGutterBase(_root, resolution.BaseRef!, resolution.Label);

        var againstBase = new EditorGutterGitService(_git, () => gutterBase).GetDiff(file);
        var againstHead = new EditorGutterGitService(_git, () => null).GetDiff(file);

        Assert.Equal(GitLineState.Added, againstBase[2]);
        Assert.Empty(againstHead);
    }

    [Fact]
    public async Task 基準で引けないときはHEADとの差分へ戻す()
    {
        await CommitAsync("a.txt", "one\n", "base");
        var file = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(file, "one\ntwo\n");

        var broken = new EditorGutterBase(_root, "no-such-ref", "壊れた基準");
        var lines = new EditorGutterGitService(_git, () => broken).GetDiff(file);

        Assert.Equal(GitLineState.Added, lines[1]);
    }

    private async Task CommitAsync(string path, string content, string message)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", message);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
