using System.IO;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ワークスペースのリポジトリ一覧（§40.4）と、リポジトリに固定した窓口（§40.5）。</summary>
public sealed class GitRepositoryCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-repo-catalog-tests", Guid.NewGuid().ToString("N"));

    public GitRepositoryCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ===== 見つけ方 =====

    [Fact]
    public void フォルダー自身がリポジトリなら_それ1つで_中は探さない()
    {
        var app = Repo("app");
        Repo(Path.Combine("app", "vendor", "lib")); // リポジトリの中の入れ子（サブモジュールではない）

        var repos = Discover(app);

        var repo = Assert.Single(repos);
        Assert.Equal(app, repo.Root);
        Assert.Equal(GitRepositoryKind.Main, repo.Kind);
        Assert.Equal(app, repo.WorkspaceFolder);
    }

    [Fact]
    public void 先祖がリポジトリなら_その根を返す()
    {
        var app = Repo("app");
        var src = Dir(Path.Combine("app", "src"));

        var repo = Assert.Single(Discover(src));
        Assert.Equal(app, repo.Root);
        Assert.Equal(src, repo.WorkspaceFolder);
    }

    [Fact]
    public void 管理外の親フォルダーの下は2段まで探し_それより奥は探さない()
    {
        var work = Dir("work");
        var api = Repo(Path.Combine("work", "api"));
        var deep = Repo(Path.Combine("work", "org", "web"));
        Repo(Path.Combine("work", "a", "b", "too-deep"));

        var repos = Discover(work);

        Assert.Equal(new[] { api, deep }, repos.Select(r => r.Root));
    }

    [Fact]
    public void ビルド出力や依存の置き場には潜らない()
    {
        var work = Dir("work");
        Repo(Path.Combine("work", "node_modules", "pkg"));
        Repo(Path.Combine("work", "bin", "x"));
        var real = Repo(Path.Combine("work", "real"));

        Assert.Equal(new[] { real }, Discover(work).Select(r => r.Root));
    }

    [Fact]
    public void リポジトリが無ければ空()
    {
        Dir(Path.Combine("plain", "docs"));
        Assert.Empty(Discover(Path.Combine(_root, "plain")));
    }

    [Fact]
    public void ワークツリーは別のリポジトリとして数え_本体と同じ共通ディレクトリを持つ()
    {
        var main = Repo("main");
        var wt = Dir("wt");
        var gitDir = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {gitDir}\n");

        var repos = Discover(main, wt);

        Assert.Equal(2, repos.Count);
        var linked = repos.Single(r => r.Root == wt);
        Assert.Equal(GitRepositoryKind.LinkedWorktree, linked.Kind);
        Assert.Equal(repos.Single(r => r.Root == main).CommonGitDir, linked.CommonGitDir);
    }

    [Fact]
    public void サブモジュールはgitmodulesから拾い_未チェックアウトは数えない()
    {
        var app = Repo("app");
        var lib = Dir(Path.Combine("app", "libs", "core"));
        Dir(Path.Combine("app", "libs", "missing"));
        File.WriteAllText(Path.Combine(lib, ".git"), "gitdir: ../../.git/modules/core\n");
        File.WriteAllText(Path.Combine(app, ".gitmodules"),
            "[submodule \"core\"]\n\tpath = libs/core\n\turl = x\n[submodule \"missing\"]\n\tpath = \"libs/missing\"\n");

        var repos = Discover(app);

        Assert.Equal(new[] { app, lib }, repos.Select(r => r.Root));
        Assert.Equal(GitRepositoryKind.Submodule, repos[1].Kind);
    }

    [Fact]
    public void フォルダーの順に並び_同じリポジトリは二度数えない()
    {
        var b = Repo("b");
        var a = Repo("a");
        var src = Dir(Path.Combine("a", "src"));

        var repos = Discover(b, a, src);

        Assert.Equal(new[] { b, a }, repos.Select(r => r.Root));
    }

    // ===== 所属 =====

    [Fact]
    public void 所属はいちばん深いリポジトリで_区切りの無い前方一致は別物()
    {
        var app = Repo("app");
        var lib = Dir(Path.Combine("app", "libs", "core"));
        File.WriteAllText(Path.Combine(lib, ".git"), "gitdir: ../../.git/modules/core\n");
        File.WriteAllText(Path.Combine(app, ".gitmodules"), "[submodule \"core\"]\n\tpath = libs/core\n");
        var app2 = Repo("app2");
        var catalog = new GitRepositoryCatalog(() => new[] { app, app2 });

        Assert.Equal(lib, catalog.RepositoryFor(Path.Combine(lib, "a.cs"))?.Root);
        Assert.Equal(app, catalog.RepositoryFor(Path.Combine(app, "libs", "b.cs"))?.Root);
        Assert.Equal(app2, catalog.RepositoryFor(Path.Combine(app2, "x.cs"))?.Root);
        Assert.Null(catalog.RepositoryFor(Path.Combine(_root, "elsewhere", "y.cs")));
        Assert.Null(catalog.RepositoryFor(null));
    }

    [Fact]
    public void フォルダーが変わると一覧を作り直す()
    {
        var a = Repo("a");
        var b = Repo("b");
        var workspace = new FakeWorkspaceService(a);
        var catalog = new GitRepositoryCatalog(workspace);
        var changed = 0;
        catalog.Changed += (_, _) => changed++;

        Assert.Equal(new[] { a }, catalog.Repositories.Select(r => r.Root));

        workspace.AddFolder(b);

        Assert.Equal(1, changed);
        Assert.Equal(new[] { a, b }, catalog.Repositories.Select(r => r.Root));
        Assert.Equal(new[] { b }, catalog.RepositoriesIn(b).Select(r => r.Root));
    }

    // ===== リポジトリに固定した窓口 =====

    [Fact]
    public async Task 固定した窓口は入れ子のリポジトリで動き_ワークスペースの対象切替に引きずられない()
    {
        var work = Dir("work");
        var api = Dir(Path.Combine("work", "api"));
        var web = Dir(Path.Combine("work", "web"));
        var workspace = new FakeWorkspaceService(work);
        var git = new GitService(workspace);
        Assert.True((await git.RunInFolderAsync(api, "init")).Success);
        Assert.True((await git.RunInFolderAsync(web, "init")).Success);
        File.WriteAllText(Path.Combine(api, "new.txt"), "x");
        git.Repositories.Refresh();

        // 親フォルダーそのものはリポジトリではない（今のワークスペースの窓口では「管理外」）。
        Assert.False((await git.GetStatusAsync()).IsRepository);

        var apiGit = git.ForPath(Path.Combine(api, "new.txt"));
        Assert.NotNull(apiGit);
        Assert.True(apiGit!.IsRepositoryScoped);
        Assert.Equal(api, apiGit.RootPath);
        Assert.Same(apiGit, git.For(api));
        Assert.Same(apiGit, git.For(web).For(api + Path.DirectorySeparatorChar));

        var status = await apiGit.GetStatusAsync();
        Assert.True(status.IsRepository);
        Assert.Contains(status.Unstaged, c => c.Path.EndsWith("new.txt", StringComparison.Ordinal));

        apiGit.SetActiveRoot(web);
        apiGit.SetActiveRootForPath(Path.Combine(web, "a.txt"));
        Assert.Equal(api, apiGit.RootPath);
        Assert.False(git.IsRepositoryScoped);
    }

    private IReadOnlyList<GitRepositoryInfo> Discover(params string[] folders)
        => GitRepositoryCatalog.Discover(folders);

    private string Dir(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    private string Repo(string relative)
    {
        var full = Dir(relative);
        Directory.CreateDirectory(Path.Combine(full, ".git"));
        return full;
    }
}
