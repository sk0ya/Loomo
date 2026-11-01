using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>全リポジトリの状態の共有（§40.6）：リポジトリ単位の状態・表示ルートでの束ね・読み込みの共有・git ディレクトリの監視。</summary>
public sealed class GitWorkspaceStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-git-ws-status", Guid.NewGuid().ToString("N"));

    public GitWorkspaceStatusTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ===== 解析（純関数） =====

    [Theory]
    [InlineData("main...origin/main [ahead 2, behind 3]", "main", "origin/main", 2, 3, false, false)]
    [InlineData("main", "main", null, 0, 0, false, false)]
    [InlineData("feat/x...origin/feat/x [gone]", "feat/x", "origin/feat/x", 0, 0, true, false)]
    [InlineData("No commits yet on main", "main", null, 0, 0, false, false)]
    [InlineData("HEAD (no branch)", "HEAD", null, 0, 0, false, true)]
    [InlineData("dev...upstream/dev [behind 1]", "dev", "upstream/dev", 0, 1, false, false)]
    public void ブランチ行を読む(string text, string branch, string? upstream, int ahead, int behind, bool gone, bool detached)
    {
        var header = GitRepositoryFileState.ParseBranchHeader(text);
        Assert.Equal(branch, header.Branch);
        Assert.Equal(upstream, header.Upstream);
        Assert.Equal(ahead, header.Ahead);
        Assert.Equal(behind, header.Behind);
        Assert.Equal(gone, header.Gone);
        Assert.Equal(detached, header.Detached);
    }

    [Fact]
    public void 状態の行から種別と数を数え_リネームの旧パスは読み飛ばす()
    {
        var root = @"C:\repo";
        var output = string.Join('\0',
            "## main...origin/main [ahead 1]",
            "M  staged.cs",
            " M work.cs",
            "MM both.cs",
            "?? new.txt",
            "UU conflict.txt",
            "R  renamed.cs", "old.cs") + '\0';

        var (statuses, header, counts) = GitRepositoryFileState.ParseStatus(root, output);

        Assert.Equal("main", header.Branch);
        Assert.Equal(1, header.Ahead);
        Assert.Equal(6, statuses.Count);
        Assert.False(statuses.ContainsKey(@"C:\repo\old.cs"));
        Assert.Equal(GitChangeKind.Staged, statuses[@"C:\repo\staged.cs"]);
        Assert.Equal(GitChangeKind.Modified, statuses[@"C:\repo\both.cs"]);
        Assert.Equal(3, counts.Staged);    // staged.cs・both.cs・renamed.cs
        Assert.Equal(2, counts.Unstaged);  // work.cs・both.cs
        Assert.Equal(1, counts.Untracked);
        Assert.Equal(1, counts.Conflicts);
    }

    [Theory]
    [InlineData("HEAD", true)]
    [InlineData("index", true)]
    [InlineData(@"refs\heads\main", true)]
    [InlineData("packed-refs", true)]
    [InlineData(@"rebase-merge\done", true)]
    [InlineData("MERGE_HEAD", true)]
    [InlineData("index.lock", false)]
    [InlineData(@"refs\heads\main.lock", false)]
    [InlineData(@"objects\ab\cdef", false)]
    [InlineData(@"logs\HEAD", false)]
    [InlineData("FETCH_HEAD", false)]
    [InlineData(@"worktrees\wt\HEAD", false)]
    public void git_ディレクトリの中で見る変化は様子に関わるものだけ(string relative, bool relevant)
    {
        var gitDir = @"C:\repo\.git";
        Assert.Equal(relevant, GitWorkspaceStatus.IsRelevantGitPath(gitDir, Path.Combine(gitDir, relative)));
    }

    // ===== 実際の git =====

    [Fact]
    public void 管理外の親フォルダーの下の複数リポジトリを束ね_それぞれの変更と所属で答える()
    {
        var work = Dir("work");
        var api = Repo(Path.Combine("work", "api"), "a.txt");
        var web = Repo(Path.Combine("work", "web"), "w.txt");
        File.WriteAllText(Path.Combine(api, "a.txt"), "changed");
        Directory.CreateDirectory(Path.Combine(web, "src"));
        File.WriteAllText(Path.Combine(web, "src", "new.cs"), "x");

        var tree = GitTreeState.Load(work, CancellationToken.None);

        Assert.True(tree.IsGitRepository);
        Assert.Equal(2, tree.Repositories.Count);
        Assert.Equal(GitChangeKind.Modified, tree.GetStatus(Path.Combine(api, "a.txt"), false));
        Assert.Equal(GitChangeKind.Untracked, tree.GetStatus(Path.Combine(web, "src", "new.cs"), false));
        Assert.Equal(GitChangeKind.DirectoryChanged, tree.GetStatus(Path.Combine(web, "src"), true));
        Assert.Equal(GitChangeKind.DirectoryChanged, tree.GetStatus(api, true));
        Assert.Equal(GitChangeKind.None, tree.GetStatus(Path.Combine(web, "w.txt"), false));
    }

    [Fact]
    public void リポジトリの状態はブランチと数と止まっている操作を持つ()
    {
        var repo = Repo("repo", "a.txt");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "changed");
        File.WriteAllText(Path.Combine(repo, "b.txt"), "new");

        var state = GitRepositoryFileState.Load(repo, CancellationToken.None);

        Assert.True(state.Loaded);
        Assert.False(string.IsNullOrEmpty(state.Branch));
        Assert.Equal(1, state.UnstagedCount);
        Assert.Equal(1, state.UntrackedCount);
        Assert.Equal(2, state.ChangedCount);
        Assert.Equal(GitRepositoryOperation.None, state.Operation);
        Assert.Contains(repo, state.ChangedDirectories); // 根の行にも「配下に変更あり」

        File.WriteAllText(Path.Combine(repo, ".git", "MERGE_HEAD"), "0000000000000000000000000000000000000000\n");
        Assert.Equal(GitRepositoryOperation.Merge, GitRepositoryFileState.Load(repo, CancellationToken.None).Operation);
    }

    [Fact]
    public async Task 同じリポジトリへの同時の読み込みは1回にまとまり_freshでなければ読み込み済みを返す()
    {
        var repo = Repo("repo", "a.txt");
        var loads = 0;
        using var status = NewStatus(new[] { repo }, (root, token) =>
        {
            Interlocked.Increment(ref loads);
            Thread.Sleep(200);
            return GitRepositoryFileState.Load(root, token);
        });

        var first = Task.Run(() => status.Load(repo, fresh: true, CancellationToken.None));
        await Task.Delay(50);
        var second = Task.Run(() => status.Load(repo, fresh: true, CancellationToken.None));
        var firstState = await first;
        var secondState = await second;

        // 2 本目は 1 本目の読み込み中に頼まれた（＝頼んだ時刻より後に読み終えた状態がある）ので読まない。
        Assert.Equal(1, loads);
        Assert.Same(firstState, secondState);
        Assert.Same(firstState, status.Load(repo, fresh: false, CancellationToken.None));
        Assert.Equal(1, loads);

        status.Load(repo, fresh: true, CancellationToken.None);
        Assert.Equal(2, loads);
    }

    [Fact]
    public void パス起点の問いは読み込み済みだけを見て_一括の問いは読んでから無視対象まで答える()
    {
        var repo = Repo("repo", "a.txt");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "out.log\n");
        File.WriteAllText(Path.Combine(repo, "out.log"), "x");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "changed");
        using var status = NewStatus(new[] { repo });

        Assert.Equal(GitChangeKind.None, status.StatusForPath(Path.Combine(repo, "a.txt"), false));

        var answers = status.StatusesForPaths(new[]
        {
            (Path.Combine(repo, "a.txt"), false),
            (Path.Combine(repo, "out.log"), false),
            (Path.Combine(_root, "elsewhere.txt"), false),
        });

        Assert.Equal(GitChangeKind.Modified, answers[Path.Combine(repo, "a.txt")]);
        Assert.Equal(GitChangeKind.Ignored, answers[Path.Combine(repo, "out.log")]);
        Assert.False(answers.ContainsKey(Path.Combine(_root, "elsewhere.txt")));
        Assert.Equal(GitChangeKind.Modified, status.StatusForPath(Path.Combine(repo, "a.txt"), false));
        Assert.Equal(GitChangeKind.Ignored, status.StatusForPath(Path.Combine(repo, "out.log"), false));
    }

    [Fact]
    public async Task ターミナルでのコミットは作業ツリーが変わらなくても知らせ_自分の読み込みでは鳴らない()
    {
        var repo = Repo("repo", "a.txt");
        File.WriteAllText(Path.Combine(repo, "b.txt"), "new");
        RunGit(repo, "add", "b.txt");
        using var status = NewStatus(new[] { repo });
        var changed = new SemaphoreSlim(0);
        var count = 0;
        status.RepositoryChanged += (_, root) =>
        {
            if (string.Equals(root, repo, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref count);
                changed.Release();
            }
        };

        Assert.Equal(1, status.Load(repo, fresh: true, CancellationToken.None).StagedCount);
        // 自分の読み込み（--no-optional-locks の status）では git ディレクトリを書かない。
        status.Load(repo, fresh: true, CancellationToken.None);
        Assert.False(await changed.WaitAsync(800));

        RunGit(repo, "commit", "-m", "second");

        Assert.True(await changed.WaitAsync(5000), "コミットが知らされない");
        Assert.Equal(0, status.Load(repo, fresh: false, CancellationToken.None).StagedCount);
    }

    private GitWorkspaceStatus NewStatus(
        IReadOnlyList<string> folders,
        Func<string, CancellationToken, GitRepositoryFileState>? loader = null)
        => new(new GitRepositoryCatalog(new FakeWorkspaceService(folders[0])), null,
            loader ?? GitRepositoryFileState.Load);

    private string Dir(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    private string Repo(string relative, string file)
    {
        var full = Dir(relative);
        RunGit(full, "init");
        RunGit(full, "config", "user.email", "loomo@example.test");
        RunGit(full, "config", "user.name", "Loomo Tests");
        File.WriteAllText(Path.Combine(full, file), "original");
        RunGit(full, "add", file);
        RunGit(full, "commit", "-m", "initial");
        return full;
    }

    private static void RunGit(string directory, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        Assert.True(process.Start());
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error.Result}");
    }
}
