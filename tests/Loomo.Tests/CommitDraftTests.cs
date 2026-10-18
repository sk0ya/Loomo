using System.IO;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>書きかけのコミットメッセージをワークスペースに残す（§24.32）。</summary>
public sealed class CommitDraftTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-commit-draft-tests", Guid.NewGuid().ToString("N"));
    private FakeWorkspaceService _workspace = null!;
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_root);
        _git = new GitService(_workspace);
        await MustRunAsync("init");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public void 帳面は末尾の区切りの違いを同じフォルダーとして扱い_空の下書きは持たない()
    {
        var book = new CommitDraftBook();
        book.Put(@"C:\work\app\", "書きかけ");

        Assert.Equal("書きかけ", book.Get(@"C:\work\app"));
        Assert.Equal("", book.Get(@"C:\work\app2"));

        book.Put(@"C:\work\app", "   ");
        Assert.Null(book.Export());
    }

    [Fact]
    public void 帳面は対象フォルダーが無ければ書かない()
    {
        var book = new CommitDraftBook();
        book.Put(null, "どこにも属さない");
        Assert.Null(book.Export());
    }

    [Fact]
    public void 復元した下書きが欄に出て_保存するとそのまま返る()
    {
        var vm = NewPanel();
        vm.RestoreCommitDrafts(new Dictionary<string, string> { [_root] = "fix: 途中まで書いた" });

        Assert.Equal("fix: 途中まで書いた", vm.CommitMessage);

        vm.CommitMessage = "fix: 続きを書いた";
        var saved = vm.CaptureCommitDrafts();
        Assert.NotNull(saved);
        Assert.Equal("fix: 続きを書いた", Assert.Single(saved!).Value);
    }

    [Fact]
    public void 別の部屋の下書きを持っていない部屋へ移ると欄は空になる()
    {
        var vm = NewPanel();
        vm.CommitMessage = "前の部屋で書いていたもの";

        // 新しい部屋の帳面は空（保存されていない）。前の部屋の欄の中身を持ち越さない。
        vm.RestoreCommitDrafts(null);

        Assert.Equal("", vm.CommitMessage);
        Assert.Null(vm.CaptureCommitDrafts());
    }

    [Fact]
    public async Task コミットしたら下書きは消える()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a");
        await MustRunAsync("add", "a.txt");
        var vm = NewPanel();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.RestoreCommitDrafts(new Dictionary<string, string> { [_root] = "add a" });

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        Assert.Equal("", vm.CommitMessage);
        Assert.Null(vm.CaptureCommitDrafts());
    }

    [Fact]
    public void Amendを立てて下ろすと立てる前の下書きが戻る()
    {
        var vm = NewPanel();
        vm.CommitMessage = "次のコミットの下書き";

        vm.Amend = true;
        // amend の間に保存しても、残るのは amend を立てる前の下書き（対象コミットの件名ではない）。
        Assert.Equal("次のコミットの下書き", Assert.Single(vm.CaptureCommitDrafts()!).Value);
        vm.Amend = false;

        Assert.Equal("次のコミットの下書き", vm.CommitMessage);
    }

    [Fact]
    public async Task コミットするファイルにエラーがあれば1回目は止めて知らせ_2回目はそのままコミットする()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "broken.cs"), "class {");
        await MustRunAsync("add", "broken.cs");
        var vm = NewPanel();
        vm.ProblemLookup = path => path.EndsWith("broken.cs", StringComparison.OrdinalIgnoreCase)
            ? new ProblemMark(Errors: 2, Warnings: 0) : null;
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.CommitMessage = "壊れたまま";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.True(vm.StatusIsError);
        Assert.Contains("エラーが 2 件", vm.StatusMessage);
        Assert.False((await _git.RunAsync("rev-parse", "--verify", "HEAD")).Success);

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.StatusIsError, vm.StatusMessage);
        Assert.True((await _git.RunAsync("rev-parse", "--verify", "HEAD")).Success);
    }

    [Fact]
    public void エラーが変わったらまた知らせる()
    {
        var errors = 1;
        var vm = NewPanel();
        vm.ProblemLookup = _ => new ProblemMark(errors, 0);

        Assert.NotNull(vm.WarnProblemsBeforeCommit(["a.cs"]));
        errors = 3;
        Assert.NotNull(vm.WarnProblemsBeforeCommit(["a.cs"]));
        Assert.Null(vm.WarnProblemsBeforeCommit(["a.cs"]));
    }

    private GitPanelViewModel NewPanel()
    {
        var compareBase = new GitCompareBaseViewModel(_git);
        return new GitPanelViewModel(
            _git, new FakeEditorService(), _workspace, new GitRootSwitchViewModel(_git, _workspace), compareBase);
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, result.Error);
        return result;
    }
}
