using System.Diagnostics;
using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ファイル一覧のリポジトリの見せ方（§40.8.2）と、.gitignore の書き先（§40.8.1）。</summary>
public sealed class FilesRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-files-repo", Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly string _api;
    private readonly string _web;
    private readonly WorkspaceService _workspace = new(new SafetySettings());
    private readonly GitRepositoryCatalog _catalog;
    private readonly GitWorkspaceStatus _status;
    private readonly GitRepositoryChips _chips;
    private readonly FolderTreeViewModel _tree;

    public FilesRepositoryTests()
    {
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.Combine(_work, "notes"));
        _api = Repo(Path.Combine("work", "api"), "a.txt");
        _web = Repo(Path.Combine("work", "web"), "w.txt");
        File.WriteAllText(Path.Combine(_api, "a.txt"), "changed");
        _workspace.OpenFolder(_work);
        _catalog = new GitRepositoryCatalog(_workspace);
        _status = new GitWorkspaceStatus(_catalog, _workspace);
        _chips = new GitRepositoryChips(_status, null, action => action());
        _tree = new FolderTreeViewModel(_workspace, new FakeAiWarmup(),
            new WorkflowStore(Path.Combine(_root, "wf")),
            new FolderTreeCommandHandler(_workspace, new FileOperationHistory()),
            new FolderTreeQuery(gitStatus: _status), repositoryChips: _chips);
        _tree.LoadRoot(_work);
    }

    public void Dispose()
    {
        _status.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ツリーを経由せずに_変更の印とリポジトリの根の行の印を出す() => OnDispatcher(async () =>
    {
        // folderTree を渡さない——サイドバーのツリーに依らず、全リポジトリの共有から受け取る。
        var column = NewColumn();
        await column.NavigateAsync(_work);

        await Until(() => Entry(column, "api").Repository is not null);
        Assert.Equal(_api, Entry(column, "api").Repository!.Root);
        Assert.Equal(_web, Entry(column, "web").Repository!.Root);
        Assert.Null(Entry(column, "notes").Repository);
        Assert.Equal(GitChangeKind.DirectoryChanged, Entry(column, "api").GitStatus);

        await column.NavigateAsync(_api);
        await Until(() => Entry(column, "a.txt").GitStatus == GitChangeKind.Modified);
        Assert.Equal(_api, column.Breadcrumbs.Last().Repository?.Root);
    });

    [Fact]
    public void 場所にはフォルダーの下の入れ子のリポジトリとリポジトリ一覧が並ぶ() => OnDispatcher(async () =>
    {
        _ = _catalog.Repositories; // 一覧は背景で作られる（実アプリではツリーの読み込みで）
        var column = NewColumn();
        await column.NavigateAsync(_work);

        column.LoadPlaces();

        var workspace = column.Places.First(g => g.Name == "ワークスペース").Items;
        Assert.Equal(FilesPlaceKind.WorkspaceFolder, workspace[0].Kind);
        Assert.Contains(workspace, p => p.Kind == FilesPlaceKind.Repository && p.FullPath == _api && p.Indent == 1 && p.Repository is not null);
        Assert.Contains(workspace, p => p.Kind == FilesPlaceKind.Repository && p.FullPath == _web);
        Assert.Equal(FilesPlaceKind.RepositoryOverview, workspace[^1].Kind);

        column.OpenPlace(workspace[^1]);
        Assert.True(column.IsRepositoryOverviewOpen);
        await Until(() => column.RepositoryOverview!.Rows.Count == 2);
        Assert.Equal(new[] { "api", "web" }, column.RepositoryOverview!.Rows.Select(r => r.Name));

        await column.NavigateAsync(_web);
        Assert.False(column.IsRepositoryOverviewOpen);
    });

    /// <summary>一覧（CollectionView）は作ったスレッドでしか書き換えられないので、本番の UI スレッドと同じく dispatcher の上で。</summary>
    private static void OnDispatcher(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var frame = new System.Windows.Threading.DispatcherFrame();
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); }
                catch (Exception exception) { error = exception; }
                finally { frame.Continue = false; }
            }));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public async Task カラムを増やすとリポジトリを先に開く()
    {
        _ = _catalog.Repositories;
        var pane = new FilesPaneViewModel(_workspace, FolderTreeCommandHandler.Unconfined(_workspace, new FileOperationHistory()),
            _tree, new FakeFilePlacesProvider(), null, null, null, _status, _chips);
        pane.Restore(snapshot: null, fallbackFolder: _work);

        pane.ColumnCount = 4;

        await Until(() => pane.Columns[2].CurrentFolder.Length > 0);
        Assert.Equal(_api, pane.Columns[1].CurrentFolder);
        Assert.Equal(_web, pane.Columns[2].CurrentFolder);
    }

    [Fact]
    public void リポジトリ一覧は数の列を降順から並べ_同値は名前順()
    {
        var rows = new[] { Row("b", 0), Row("a", 3), Row("c", 3) };

        var byChanges = RepositoryOverviewViewModel.Sort(rows, "Changes", descending: true);
        Assert.Equal(new[] { "a", "c", "b" }, byChanges.Select(r => r.Name));
        Assert.Equal(new[] { "a", "b", "c" }, RepositoryOverviewViewModel.Sort(rows, "Name", false).Select(r => r.Name));
    }

    [Fact]
    public async Task gitignore_は属するリポジトリの根に書き_入れ子のリポジトリ自身は外側に書く()
    {
        Directory.CreateDirectory(Path.Combine(_api, "out"));
        await _tree.WhenTreeLoadedAsync();

        _tree.AddToGitignore(Path.Combine(_api, "out"), isDirectory: true);

        Assert.True(File.Exists(Path.Combine(_api, ".gitignore")));
        Assert.Contains("out/", File.ReadAllText(Path.Combine(_api, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(_work, ".gitignore"))); // 以前はどのリポジトリでもない work に書いていた
        Assert.False(_tree.CanAddToGitignoreForPath(_api)); // 外側にリポジトリが無ければ入れ子の根は対象外
    }

    private RepositoryOverviewRow Row(string name, int changes)
    {
        var dir = Path.Combine(_root, "rows", name);
        Directory.CreateDirectory(dir);
        var state = changes == 0 ? null : FakeState(dir, changes);
        return new RepositoryOverviewRow(new GitRepositoryInfo(dir, GitRepositoryKind.Main, dir, null),
            new RepositoryChipViewModel(dir, null), name, state, null);
    }

    private GitRepositoryFileState FakeState(string dir, int changes)
    {
        Git(dir, "init");
        for (var i = 0; i < changes; i++)
            File.WriteAllText(Path.Combine(dir, $"f{i}.txt"), "x");
        return GitRepositoryFileState.Load(dir, CancellationToken.None);
    }

    private FilesColumnViewModel NewColumn()
        => new(_workspace, FolderTreeCommandHandler.Unconfined(_workspace, new FileOperationHistory()), _tree,
            new FakeFilePlacesProvider(), folderTree: null, gitStatus: _status, repositoryChips: _chips);

    private static FileEntryViewModel Entry(FilesColumnViewModel column, string name)
        => column.Entries.Single(e => e.Name == name);

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                Assert.Fail("時間内に条件が満たされなかった");
            await Task.Delay(50);
        }
    }

    private string Repo(string relative, string file)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(full);
        Git(full, "init");
        Git(full, "config", "user.email", "loomo@example.test");
        Git(full, "config", "user.name", "Loomo Tests");
        File.WriteAllText(Path.Combine(full, file), "original");
        Git(full, "add", file);
        Git(full, "commit", "-m", "initial");
        return full;
    }

    private static void Git(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
    }
}
