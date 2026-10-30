using System.IO;
using Editor.Controls.Git;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// エディタの git（§24.11.1）。ガターは共有の比較基準に合わせる・blame 等はワークツリーでも出る。
/// 解析は純粋に、git を通す部分は実 git で確かめる。
/// </summary>
public sealed class EditorGitServiceTests : IAsyncLifetime
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
        Assert.Equal(inside, EditorGitService.IsInside(folder, file));

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

        var againstBase = new EditorGitService(_git, () => gutterBase).GetDiff(file);
        var againstHead = new EditorGitService(_git, () => null).GetDiff(file);

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
        var lines = new EditorGitService(_git, () => broken).GetDiff(file);

        Assert.Equal(GitLineState.Added, lines[1]);
    }

    // ===== ワークツリー（.git がファイル）でもエディタの git が効く =====

    [Fact]
    public async Task ワークツリーで開いたファイルにもblameとブランチ名とガターが出る()
    {
        await CommitAsync("a.txt", "one\ntwo\n", "最初のコミット");
        var worktree = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-wt");
        await MustRunAsync("worktree", "add", "-b", "topic", worktree);
        try
        {
            Assert.True(File.Exists(Path.Combine(worktree, ".git")));
            var file = Path.Combine(worktree, "a.txt");
            await File.WriteAllTextAsync(file, "one\ntwo\nthree\n");
            var service = new EditorGitService(_git, () => null);

            var blame = service.GetBlameLines(file);
            Assert.Equal(2, blame.Count);
            Assert.Equal("Loomo Test", blame[0].Author);
            Assert.Equal("最初のコミット", blame[1].Summary);

            Assert.Equal("topic", service.GetBranchName(file));
            Assert.Equal(GitLineState.Added, service.GetDiff(file)[2]);
            Assert.Contains("a.txt", service.GetStatusOutput(file));
        }
        finally
        {
            await _git.RunAsync("worktree", "remove", "--force", worktree);
            try { Directory.Delete(worktree, recursive: true); } catch { /* 消えていれば良い */ }
        }
    }

    [Fact]
    public void リポジトリの外ではblameもブランチ名も空()
    {
        var outside = Path.Combine(Path.GetTempPath(), "loomo-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var file = Path.Combine(outside, "a.txt");
            File.WriteAllText(file, "one\n");
            var service = new EditorGitService(_git, () => null);

            Assert.Empty(service.GetBlameLines(file));
            Assert.Null(service.GetBranchName(file));
            Assert.Empty(service.GetDiff(file));
            Assert.Equal("(not a git repository)", service.GetStatusOutput(file));
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task カーソルのハンクだけをステージし戻せる()
    {
        await CommitAsync("a.txt", string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line{i}")) + "\n", "base");
        var file = Path.Combine(_root, "a.txt");
        var lines = Enumerable.Range(1, 20).Select(i => $"line{i}").ToArray();
        lines[1] = "変更2";
        lines[17] = "変更18";
        await File.WriteAllTextAsync(file, string.Join('\n', lines) + "\n");
        var service = new EditorGitService(_git, () => null);

        var staged = service.StageHunk(file, 1);
        Assert.True(staged.Success, staged.Output);
        var cached = await MustRunAsync("diff", "--cached");
        Assert.Contains("+変更2", cached.Output);
        Assert.DoesNotContain("変更18", cached.Output);

        var unstaged = service.UnstageHunk(file, 1);
        Assert.True(unstaged.Success, unstaged.Output);
        Assert.Equal("", (await MustRunAsync("diff", "--cached")).Output.Trim());
    }

    [Fact]
    public void porcelainのblameはメタが省かれた行にも同じコミットを付け未コミット行は除く()
    {
        const string hash = "1111111111111111111111111111111111111111";
        const string zero = "0000000000000000000000000000000000000000";
        var porcelain = string.Join('\n',
            $"{hash} 1 1 2",
            "author koya",
            "author-time 1750000000",
            "summary 最初",
            "filename a.txt",
            "\tone",
            $"{hash} 2 2",
            "\ttwo",
            $"{zero} 3 3 1",
            "author Not Committed Yet",
            "summary Version of a.txt from a.txt",
            "\tthree",
            "");

        var blame = GitBlamePorcelainParser.Parse(porcelain);

        Assert.Equal(2, blame.Count);
        Assert.Equal("1111111", blame[1].CommitHash);
        Assert.Equal("koya", blame[1].Author);
        Assert.Equal("最初", blame[1].Summary);
        Assert.Equal(2, blame[1].OriginalLine);
        Assert.False(blame.ContainsKey(2));
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
