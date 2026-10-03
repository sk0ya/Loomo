using System.IO;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 実際の git に対するワークツリーの作成・一覧・スナップショット・比較・削除。
/// 置き場所は本番と同じ「リポジトリの隣の <c>&lt;名前&gt;.worktrees</c>」にする。
/// </summary>
public sealed class GitWorktreeServiceTests : IAsyncLifetime
{
    private readonly string _base =
        Path.Combine(Path.GetTempPath(), "loomo-git-worktree", Guid.NewGuid().ToString("N"));
    private string _root = null!;
    private FakeWorkspaceService _workspace = null!;
    private GitService _git = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(_base, "repo");
        Directory.CreateDirectory(_root);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_root);
        _git = new GitService(_workspace);
        await MustRunAsync("init");
        await MustRunAsync("symbolic-ref", "HEAD", "refs/heads/main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
        await MustRunAsync("config", "core.autocrlf", "false");
        await CommitAsync("a.txt", "a\n", "first");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    private string Suggest(string branch) => GitWorktreeArgs.SuggestPath(_root, branch);

    private async Task<string> AddFeatureWorktreeAsync(string branch = "feature")
    {
        var path = Suggest(branch);
        var result = await _git.AddWorktreeAsync(
            new GitWorktreeAddRequest(GitWorktreeAddMode.NewBranch, path, branch, "main"));
        Assert.True(result.Success, result.Message);
        return path;
    }

    [Fact]
    public async Task 作成したワークツリーが一覧に出ていまのものが分かる()
    {
        var path = await AddFeatureWorktreeAsync();

        var list = await _git.GetWorktreesAsync(includeChangeCounts: true);

        Assert.Equal(2, list.Count);
        var main = list[0];
        Assert.True(main.IsMain);
        Assert.True(main.IsCurrent);
        Assert.Equal(0, main.ChangeCount);
        var feature = Assert.Single(list, w => w.Branch == "feature");
        Assert.True(feature.IsSamePath(path));
        Assert.False(feature.IsCurrent);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task 未コミットの件数は未追跡も数える()
    {
        var path = await AddFeatureWorktreeAsync();
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(path, "new.txt"), "new\n");

        var feature = Assert.Single(await _git.GetWorktreesAsync(includeChangeCounts: true), w => w.Branch == "feature");

        Assert.Equal(2, feature.ChangeCount);
        Assert.True(feature.IsDirty);
    }

    [Fact]
    public async Task スナップショットは相手のインデックスに触らず未追跡まで固める()
    {
        var path = await AddFeatureWorktreeAsync();
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "changed\n");
        await File.WriteAllTextAsync(Path.Combine(path, "new.txt"), "new\n");
        var statusBefore = await RunInAsync(path, "status", "--porcelain");

        var first = await _git.SnapshotWorktreeAsync(path);
        var second = await _git.SnapshotWorktreeAsync(path);

        Assert.True(first.Success, first.Error);
        Assert.Equal(first.Tree, second.Tree);   // 同じ内容なら同じハッシュ（一覧がちらつかない）
        Assert.Equal(statusBefore, await RunInAsync(path, "status", "--porcelain"));   // 相手は何も変わらない
        var files = await _git.RunAsync("ls-tree", "--name-only", first.Tree!);
        Assert.Contains("new.txt", files.Output);
    }

    [Fact]
    public async Task ワークツリー基準は向こうの作業中の状態と比べる()
    {
        var path = await AddFeatureWorktreeAsync();
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "theirs\n");
        await File.WriteAllTextAsync(Path.Combine(path, "only-there.txt"), "x\n");

        var resolution = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.Worktree, path));
        Assert.Null(resolution.Error);
        var changes = await _git.GetCompareChangesAsync(resolution.BaseRef!);

        Assert.Null(changes.Error);
        // 左＝向こう、右＝こちらの作業ツリー：向こうにしか無いファイルはこちらから見て「削除」。
        Assert.Contains(changes.Files, f => f.Path == "a.txt" && f.Status == 'M');
        Assert.Contains(changes.Files, f => f.Path == "only-there.txt" && f.Status == 'D');
        var diff = await _git.GetCompareFileDiffAsync(resolution.BaseRef!, changes.Files.First(f => f.Path == "a.txt"));
        Assert.Contains("-theirs", diff);
        Assert.Contains("+a", diff);
    }

    [Fact]
    public async Task ワークツリー基準は毎回固め直す()
    {
        var path = await AddFeatureWorktreeAsync();
        var selection = new GitCompareBaseSelection(GitCompareBaseKind.Worktree, path);
        var before = await _git.ResolveCompareBaseAsync(selection);

        // こちらのリポジトリ監視には現れない所での編集。
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "later\n");
        var after = await _git.ResolveCompareBaseAsync(selection);

        Assert.NotEqual(before.BaseRef, after.BaseRef);
    }

    [Fact]
    public async Task 自分自身や存在しないワークツリーは理由付きで失敗する()
    {
        var self = await _git.ResolveCompareBaseAsync(new GitCompareBaseSelection(GitCompareBaseKind.Worktree, _root));
        Assert.Null(self.BaseRef);
        Assert.Contains("自身", self.Error);

        var missing = await _git.ResolveCompareBaseAsync(
            new GitCompareBaseSelection(GitCompareBaseKind.Worktree, Path.Combine(_base, "nowhere")));
        Assert.Null(missing.BaseRef);
        Assert.Contains("見つかりません", missing.Error);

        var none = await _git.ResolveCompareBaseAsync(new GitCompareBaseSelection(GitCompareBaseKind.Worktree, null));
        Assert.NotNull(none.Error);
    }

    [Fact]
    public async Task リビジョン基準はタグやHEADの相対指定を解決する()
    {
        var first = (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
        await MustRunAsync("tag", "v1");
        await CommitAsync("b.txt", "b\n", "second");

        var byTag = await _git.ResolveCompareBaseAsync(new GitCompareBaseSelection(GitCompareBaseKind.Revision, "v1"));
        var byRelative = await _git.ResolveCompareBaseAsync(new GitCompareBaseSelection(GitCompareBaseKind.Revision, "HEAD~1"));
        var missing = await _git.ResolveCompareBaseAsync(new GitCompareBaseSelection(GitCompareBaseKind.Revision, "nope"));

        Assert.Equal(first, byTag.BaseRef);
        Assert.Equal(first, byRelative.BaseRef);
        Assert.Null(missing.BaseRef);
        Assert.Contains("nope", missing.Error);
        var changes = await _git.GetCompareChangesAsync(byTag.BaseRef!);
        Assert.Contains(changes.Files, f => f.Path == "b.txt" && f.Status == 'A');
    }

    [Fact]
    public async Task 二点比較はワークツリーの作業中どうしでも分岐点からでも引ける()
    {
        var featurePath = await AddFeatureWorktreeAsync();
        await RunInAsync(featurePath, "commit", "--allow-empty", "-m", "noop");
        await File.WriteAllTextAsync(Path.Combine(featurePath, "wip.txt"), "wip\n");   // 未コミット・未追跡
        await CommitAsync("main-only.txt", "m\n", "main moves on");                    // main 側だけ進む
        var feature = Assert.Single(await _git.GetWorktreesAsync(), w => w.Branch == "feature");

        var plain = await _git.ResolveCompareRangeAsync(
            GitCompareEndpoint.Ref("main"), GitCompareEndpoint.Worktree(feature), fromMergeBase: false);
        Assert.Null(plain.Error);
        var plainFiles = (await _git.GetRangeChangesAsync(plain.FromRef, plain.ToRef!)).Files;
        Assert.Contains(plainFiles, f => f.Path == "wip.txt" && f.Status == 'A');
        Assert.Contains(plainFiles, f => f.Path == "main-only.txt" && f.Status == 'D');

        // 分岐点から：feature が分かれた後に入れたもの（未コミット含む）だけ。main の前進は出ない。
        var fromBase = await _git.ResolveCompareRangeAsync(
            GitCompareEndpoint.Ref("main"), GitCompareEndpoint.Worktree(feature), fromMergeBase: true);
        Assert.Null(fromBase.Error);
        var baseFiles = (await _git.GetRangeChangesAsync(fromBase.FromRef, fromBase.ToRef!)).Files;
        Assert.Contains(baseFiles, f => f.Path == "wip.txt");
        Assert.DoesNotContain(baseFiles, f => f.Path == "main-only.txt");
    }

    [Fact]
    public async Task ブランチ名と同名のフォルダーがあっても二点比較の一覧が引ける()
    {
        await MustRunAsync("branch", "docs");
        await CommitAsync("docs/readme.md", "x\n", "docs folder");   // ブランチ docs と同名のフォルダー

        var range = await _git.ResolveCompareRangeAsync(
            GitCompareEndpoint.Ref("docs"), GitCompareEndpoint.Ref("main"), fromMergeBase: false);
        var files = (await _git.GetRangeChangesAsync(range.FromRef, range.ToRef!)).Files;

        Assert.Contains(files, f => f.Path == "docs/readme.md");
        var named = (await _git.GetRangeChangesAsync("docs", "main")).Files;   // 名前のまま渡しても曖昧にならない
        Assert.Contains(named, f => f.Path == "docs/readme.md");
    }

    [Fact]
    public async Task 範囲の一覧が引けないときは変更なしと名乗らず理由を返す()
    {
        var changes = await _git.GetRangeChangesAsync("no-such-ref", "main");

        Assert.True(changes.HasError);
        Assert.False(changes.IsCanceled);
        Assert.Empty(changes.Files);
    }

    [Fact]
    public async Task 中止された範囲の一覧は中止として返り例外にならない()
    {
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(_root);
        var history = new GitHistoryService(new GitCommandRunner(new GitRootState(workspace)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var changes = await history.GetRangeChangesAsync("main~0", "main", canceled.Token);

        Assert.True(changes.IsCanceled);
        Assert.Same(GitCompareCancellation.Message, changes.Error);
    }

    [Fact]
    public async Task ブランチ元は作ったときの起点を覚えている()
    {
        await MustRunAsync("branch", "release");
        var path = Suggest("feature");
        var added = await _git.AddWorktreeAsync(
            new GitWorktreeAddRequest(GitWorktreeAddMode.NewBranch, path, "feature", "release"));
        Assert.True(added.Success, added.Message);
        var feature = Assert.Single(await _git.GetWorktreesAsync(), w => w.Branch == "feature");

        var origin = await _git.ResolveBranchOriginAsync(feature);

        Assert.Equal(new GitBranchOrigin("release", GitBranchOriginSource.Recorded), origin);
    }

    [Fact]
    public async Task 起点を省いて作ったブランチはそのときの枝を元として覚える()
    {
        var path = Suggest("topic");
        var added = await _git.AddWorktreeAsync(
            new GitWorktreeAddRequest(GitWorktreeAddMode.NewBranch, path, "topic", null));
        Assert.True(added.Success, added.Message);
        var topic = Assert.Single(await _git.GetWorktreesAsync(), w => w.Branch == "topic");

        var origin = await _git.ResolveBranchOriginAsync(topic);

        Assert.Equal(new GitBranchOrigin("main", GitBranchOriginSource.Recorded), origin);
    }

    [Fact]
    public async Task 外で作ったブランチはreflogから元を拾い消えた元は飛ばす()
    {
        await MustRunAsync("branch", "release");
        await MustRunAsync("branch", "topic", "release");   // CLI で作った＝Loomo は何も覚えていない
        var added = await _git.AddWorktreeAsync(
            new GitWorktreeAddRequest(GitWorktreeAddMode.ExistingBranch, Suggest("topic"), "topic", null));
        Assert.True(added.Success, added.Message);
        var topic = Assert.Single(await _git.GetWorktreesAsync(), w => w.Branch == "topic");

        Assert.Equal(new GitBranchOrigin("release", GitBranchOriginSource.Reflog),
            await _git.ResolveBranchOriginAsync(topic));

        // 元の枝が消されたら既定ブランチへ落ちる（実在しない元で分岐点を求めて失敗させない）。
        await MustRunAsync("branch", "-D", "release");
        _git.InvalidateReadCache();
        Assert.Equal(new GitBranchOrigin("main", GitBranchOriginSource.DefaultBranch),
            await _git.ResolveBranchOriginAsync(topic));
    }

    [Fact]
    public async Task ブランチ元からの比較は元が進んでも自分の変更だけを出す()
    {
        var featurePath = await AddFeatureWorktreeAsync();
        await File.WriteAllTextAsync(Path.Combine(featurePath, "wip.txt"), "wip\n");   // 未コミット・未追跡
        await CommitAsync("main-only.txt", "m\n", "main moves on");
        var feature = Assert.Single(await _git.GetWorktreesAsync(), w => w.Branch == "feature");
        var origin = await _git.ResolveBranchOriginAsync(feature);

        var range = await _git.ResolveCompareRangeAsync(
            GitCompareEndpoint.Ref(origin!.Reference), GitCompareEndpoint.Worktree(feature), fromMergeBase: true);
        var files = (await _git.GetRangeChangesAsync(range.FromRef, range.ToRef!)).Files;

        Assert.Contains(files, f => f.Path == "wip.txt" && f.Status == 'A');
        Assert.DoesNotContain(files, f => f.Path == "main-only.txt");
    }

    [Fact]
    public async Task 変更のあるワークツリーは強制でないと消えずブランチは残る()
    {
        var path = await AddFeatureWorktreeAsync();
        await File.WriteAllTextAsync(Path.Combine(path, "dirty.txt"), "x\n");

        var refused = await _git.RemoveWorktreeAsync(path, force: false);
        Assert.False(refused.Success);
        Assert.True(GitWorktreeArgs.RemoveNeedsForce(refused.Message), refused.Message);

        var removed = await _git.RemoveWorktreeAsync(path, force: true);
        Assert.True(removed.Success, removed.Message);
        Assert.False(Directory.Exists(path));
        Assert.Single(await _git.GetWorktreesAsync());
        Assert.Contains(await _git.GetBranchesAsync(), b => b.Name == "feature");
    }

    [Fact]
    public async Task フォルダーを消されたワークツリーは掃除で一覧から外れる()
    {
        var path = await AddFeatureWorktreeAsync();
        Directory.Delete(path, recursive: true);

        var stale = Assert.Single(await _git.GetWorktreesAsync(), w => w.Branch == "feature");
        Assert.False(stale.Exists);

        var prune = await _git.PruneWorktreesAsync();
        Assert.True(prune.Success, prune.Message);
        Assert.Single(await _git.GetWorktreesAsync());
    }

    [Fact]
    public async Task ロックとロック解除()
    {
        var path = await AddFeatureWorktreeAsync();

        Assert.True((await _git.LockWorktreeAsync(path, "USB")).Success);
        var locked = Assert.Single(await _git.GetWorktreesAsync(), w => w.Branch == "feature");
        Assert.True(locked.IsLocked);
        Assert.Equal("USB", locked.LockReason);

        Assert.True((await _git.UnlockWorktreeAsync(path)).Success);
        Assert.False(Assert.Single(await _git.GetWorktreesAsync(), w => w.Branch == "feature").IsLocked);
    }

    [Fact]
    public async Task ワークツリーの中から開いてもいまのものを取り違えない()
    {
        var path = await AddFeatureWorktreeAsync();
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(path);
        var git = new GitService(workspace);

        var list = await git.GetWorktreesAsync();

        Assert.True(Assert.Single(list, w => w.IsCurrent).IsSamePath(path));
        // 向こう（メイン）と比べられる。
        var resolution = await git.ResolveCompareBaseAsync(new GitCompareBaseSelection(GitCompareBaseKind.Worktree, _root));
        Assert.Null(resolution.Error);
    }

    [Fact]
    public async Task 比較基準のVMはワークツリーを候補に出し自分自身は出さない()
    {
        var path = await AddFeatureWorktreeAsync();
        var vm = new GitCompareBaseViewModel(_git);
        var changed = 0;
        vm.Changed += (_, _) => changed++;

        vm.Apply(new GitCompareBaseSelection(GitCompareBaseKind.Worktree, path));
        await vm.ReloadWorktreesAsync();

        Assert.True(vm.NeedsWorktree);
        Assert.False(vm.NeedsBranch);
        var option = Assert.Single(vm.WorktreeOptions);
        Assert.True(option.IsSamePath(path));
        Assert.Equal(path, vm.SelectedWorktree);
        Assert.Equal(1, changed);   // 種別と対象をまとめて書いたので通知は1回

        // 種別ごとに対象を別に持つ：ブランチへ行って戻ってもワークツリーの選択は残る。
        vm.Apply(new GitCompareBaseSelection(GitCompareBaseKind.Branch, "main"));
        vm.Apply(new GitCompareBaseSelection(GitCompareBaseKind.Worktree, path));
        var snapshot = vm.Capture();
        Assert.Equal((int)GitCompareBaseKind.Worktree, snapshot.Kind);
        Assert.Equal(path, snapshot.Worktree);
        Assert.Equal("main", snapshot.Branch);
    }

    private async Task<string> RunInAsync(string directory, params string[] args)
    {
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(directory);
        var result = await new GitService(workspace).RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result.Output;
    }

    private async Task<string> CommitAsync(string path, string content, string message)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
        await MustRunAsync("add", "-A");
        await MustRunAsync("commit", "-m", message);
        return (await MustRunAsync("rev-parse", "HEAD")).Output.Trim();
    }

    private async Task<GitCommandResult> MustRunAsync(params string[] args)
    {
        var result = await _git.RunAsync(args);
        Assert.True(result.Success, $"git {string.Join(' ', args)}: {result.Message}");
        return result;
    }
}
