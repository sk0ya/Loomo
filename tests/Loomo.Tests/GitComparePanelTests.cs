using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 比較基準を切り替えたとき、変更ファイル一覧と差分本体の<b>両方</b>がその基準になり、
/// 作業ツリー固有の操作（ステージ／破棄／行単位の適用）が出なくなることを確かめる。
/// </summary>
public sealed class GitComparePanelTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "loomo-git-compare-panel", Guid.NewGuid().ToString("N"));
    private FakeWorkspaceService _workspace = null!;
    private GitService _git = null!;
    private GitCompareBaseViewModel _compareBase = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _workspace = new FakeWorkspaceService();
        _workspace.OpenFolder(_root);
        _git = new GitService(_workspace);
        _compareBase = new GitCompareBaseViewModel(_git);
        await MustRunAsync("init");
        // 既定ブランチ名は git のバージョン・設定で変わる（init -b は 2.28+ を要求する）。
        await MustRunAsync("symbolic-ref", "HEAD", "refs/heads/main");
        await MustRunAsync("config", "user.name", "Loomo Test");
        await MustRunAsync("config", "user.email", "loomo@example.invalid");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* git の解放待ちは無視 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ブランチ基準へ切り替えると一覧が基準の差分になり作業ツリー操作は出ない()
    {
        await CommitAsync("shared.txt", "base\n", "first");
        await MustRunAsync("checkout", "-b", "feature");
        await CommitAsync("feature-only.txt", "mine\n", "feature work");
        await File.WriteAllTextAsync(Path.Combine(_root, "untracked.txt"), "new");

        var panel = CreatePanel();
        await panel.RefreshCommand.ExecuteAsync(null);

        // まずは既定＝作業ツリー基準。未追跡が並び、ステージ等の操作は出る。
        Assert.True(panel.Capabilities.CanStage);
        Assert.True(panel.Capabilities.CanCommit);
        Assert.Contains(panel.UnversionedFiles, i => i.Entry.Path == "untracked.txt");

        SelectBase(GitCompareBaseKind.MergeBase, "main");
        await panel.RefreshCommand.ExecuteAsync(null);

        // 一覧は「このブランチで入れた変更」だけ。未追跡は基準比較には現れない。
        Assert.False(panel.Capabilities.CanStage);
        Assert.False(panel.Capabilities.CanDiscard);
        Assert.False(panel.Capabilities.CanCommit);
        Assert.Empty(panel.Staged);
        Assert.Empty(panel.UnversionedFiles);
        var section = Assert.Single(panel.WorkingTreeSections);
        Assert.Contains("main", section.Name);
        Assert.Equal(new[] { "feature-only.txt" }, LeafPaths(section));
    }

    [Fact]
    public async Task 基準がブランチのときはステージも破棄もコマンド自体が何もしない()
    {
        await CommitAsync("a.txt", "a\n", "first");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a\nedited\n");

        var panel = CreatePanel();
        await panel.RefreshCommand.ExecuteAsync(null);
        var item = Assert.Single(panel.Changes);

        SelectBase(GitCompareBaseKind.Branch, "main");
        await panel.RefreshCommand.ExecuteAsync(null);

        // ビュー側では項目ごと出さないが、コマンドを直接叩いてもインデックスは動かさない。
        await panel.StageCommand.ExecuteAsync(item);
        await panel.UnstageAllCommand.ExecuteAsync(null);
        var status = await _git.GetStatusAsync();
        Assert.Empty(status.Staged);
    }

    [Fact]
    public async Task Diffペインの一覧と差分本体も同じ基準になり破棄は消える()
    {
        await CommitAsync("shared.txt", "base\n", "first");
        await MustRunAsync("checkout", "-b", "feature");
        // コミットしていない編集。二点記法なので基準比較にも出る。
        await File.WriteAllTextAsync(Path.Combine(_root, "shared.txt"), "base\nedited\n");

        var diff = CreateDiff();
        diff.EnsureLoaded();
        await WaitAsync(() => diff.Files.Count > 0);
        Assert.True(diff.CanDiscardSelected);   // 作業ツリー基準では破棄できる

        SelectBase(GitCompareBaseKind.MergeBase, "main");
        await WaitAsync(() => diff.Files.Count > 0 && diff.Files[0].CompareBaseFile is not null);

        var file = Assert.Single(diff.Files);
        Assert.Equal("shared.txt", file.DisplayPath);
        Assert.NotNull(file.CompareBaseFile);
        // 破棄・行単位の適用は「作業ツリー vs インデックス／HEAD」の概念なので消える。
        Assert.False(diff.CanDiscardSelected);
        Assert.False(diff.CanDiscardLines);
        Assert.False(diff.CanStageHunks);

        await WaitAsync(() => diff.SideRows.Any(r => r.RightText.Contains("edited")));
    }

    [Fact]
    public async Task 基準を解決できないときは一覧を空にして理由を出す()
    {
        await CommitAsync("a.txt", "a\n", "first");

        var panel = CreatePanel();
        await panel.RefreshCommand.ExecuteAsync(null);

        SelectBase(GitCompareBaseKind.Branch, "存在しない枝");
        await panel.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(panel.WorkingTreeSections);
        // 基準そのものを解決できなかった理由は基準選択 UI が出す（一覧側の CompareBaseError は
        // 「一覧の取得が失敗した」ときだけ＝同じ理由を2箇所に出さない）。
        Assert.Contains("存在しない枝", _compareBase.ErrorMessage);
        Assert.True(_compareBase.HasError);
        Assert.Equal("", panel.CompareBaseError);
    }

    [Fact]
    public async Task 基準の切替は購読側へ通知される()
    {
        await CommitAsync("a.txt", "a\n", "first");
        await _compareBase.ReloadBranchesAsync();

        var notified = 0;
        _compareBase.Changed += (_, _) => notified++;

        _compareBase.SelectedMode = _compareBase.ModeOptions.First(o => o.Kind == GitCompareBaseKind.Branch);
        Assert.True(notified >= 1);

        var before = notified;
        _compareBase.ResetToWorkingTree();
        Assert.True(notified > before);
        Assert.True(_compareBase.IsWorkingTree);
    }

    [Fact]
    public async Task 既定ブランチは切替時に自動で選ばれる()
    {
        await CommitAsync("a.txt", "a\n", "first");
        await _compareBase.ReloadBranchesAsync();

        Assert.Equal("main", _compareBase.SelectedBranch);
        Assert.Contains("main", _compareBase.BranchOptions);
    }

    [Fact]
    public async Task 選んでいたブランチが消えたら選択も外れる()
    {
        await CommitAsync("a.txt", "a\n", "first");
        await MustRunAsync("branch", "topic");
        SelectBase(GitCompareBaseKind.Branch, "topic");

        await MustRunAsync("branch", "-D", "topic");
        await _compareBase.ReloadBranchesAsync();

        Assert.NotEqual("topic", _compareBase.SelectedBranch);
    }

    [Fact]
    public async Task 復元は種別とブランチを取り戻す()
    {
        await CommitAsync("a.txt", "a\n", "first");
        SelectBase(GitCompareBaseKind.MergeBase, "main");

        var snapshot = _compareBase.Capture();
        Assert.Equal((int)GitCompareBaseKind.MergeBase, snapshot.Kind);
        Assert.Equal("main", snapshot.Branch);

        var restored = new GitCompareBaseViewModel(_git);
        restored.Restore(snapshot);

        Assert.Equal(GitCompareBaseKind.MergeBase, restored.SelectedMode.Kind);
        Assert.Equal("main", restored.SelectedBranch);
        Assert.False(restored.IsWorkingTree);

        // 旧データ（null）は作業ツリー基準へ落とす。
        var legacy = new GitCompareBaseViewModel(_git);
        legacy.Restore(null);
        Assert.True(legacy.IsWorkingTree);
    }

    [Fact]
    public async Task 復元した枝は候補がまだ読めていなくても失われない()
    {
        // 起動時の実際の順番：ワークスペース復元 → FolderTree.LoadRoot（＝OpenFolder）。
        // 復元が先に走ると Git の対象フォルダーがまだ無く、候補は空で返る。そこで保存値を捨てると
        // 保存した枝が既定ブランチへすり替わる（種別だけ残って比較先が別物になる）。
        await CommitAsync("a.txt", "a\n", "first");
        await MustRunAsync("branch", "develop");

        var pending = new FakeWorkspaceService();          // まだフォルダーを開いていない
        var pendingGit = new GitService(pending);
        var vm = new GitCompareBaseViewModel(pendingGit);

        vm.Restore(new GitCompareSnapshot { Kind = (int)GitCompareBaseKind.MergeBase, Branch = "develop" });
        await vm.ReloadBranchesAsync();                    // 候補は空（リポジトリ未オープン）
        Assert.Empty(vm.BranchOptions);
        Assert.Equal("develop", vm.Capture().Branch);      // 保存し直しても失わない

        pending.OpenFolder(_root);                         // ここで初めて対象が決まる
        await vm.ReloadBranchesAsync();

        Assert.Equal("develop", vm.SelectedBranch);
        Assert.Equal(GitCompareBaseKind.MergeBase, vm.SelectedMode.Kind);
    }

    [Fact]
    public async Task 候補の差し替えで選択がnullに戻されても選び直しはしない()
    {
        // WPF の Selector は ItemsSource を差し替えると選択をクリアし、TwoWay の SelectedItem へ
        // null を書き戻す。その書き戻しを購読側で再現して、既定ブランチへ勝手に移らないことを見る。
        await CommitAsync("a.txt", "a\n", "first");
        await MustRunAsync("branch", "develop");
        SelectBase(GitCompareBaseKind.Branch, "develop");
        await _compareBase.ReloadBranchesAsync();
        Assert.Equal("develop", _compareBase.SelectedBranch);

        _compareBase.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GitCompareBaseViewModel.BranchOptions))
                _compareBase.SelectedBranch = null;        // Selector の書き戻しを模す
        };

        await MustRunAsync("branch", "topic");             // 候補が1本増える＝差し替えが起きる
        await _compareBase.ReloadBranchesAsync();

        Assert.Equal("develop", _compareBase.SelectedBranch);
    }

    [Fact]
    public async Task リポジトリを切り替えても実在する枝の選択は保たれる()
    {
        await CommitAsync("a.txt", "a\n", "first");
        await MustRunAsync("branch", "develop");
        SelectBase(GitCompareBaseKind.Branch, "develop");
        await _compareBase.ReloadBranchesAsync();

        // マルチルートの対象切替（Git のルートは PrimaryFolder とは別概念）。
        _workspace.AddFolder(_root);
        _git.SetActiveRoot(_root);
        await _compareBase.ReloadBranchesAsync();

        Assert.Equal("develop", _compareBase.SelectedBranch);
    }

    [Fact]
    public async Task 基準を解決できないときはDiffペインも空にして理由を出す()
    {
        await CommitAsync("a.txt", "a\n", "first");

        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a\nedited\n");

        var diff = CreateDiff();
        diff.EnsureLoaded();
        await WaitAsync(() => diff.Files.Count > 0);

        SelectBase(GitCompareBaseKind.Branch, "存在しない枝");
        await WaitAsync(() => diff.Files.Count == 0);

        // 黙って作業ツリーへ落とさず、なぜ空なのかを一覧の空メッセージに出す。
        Assert.Contains("存在しない枝", diff.EmptyMessage);
        Assert.False(diff.CanDiscardSelected);
        Assert.False(diff.CanDiscardLines);
    }

    [Fact]
    public async Task 基準の切替中にリポジトリ変更が割り込んでも最後は新しい基準になる()
    {
        await CommitAsync("shared.txt", "base\n", "first");
        await MustRunAsync("checkout", "-b", "feature");
        await CommitAsync("feature-only.txt", "mine\n", "feature work");

        var panel = CreatePanel();
        await panel.RefreshCommand.ExecuteAsync(null);

        SelectBase(GitCompareBaseKind.MergeBase, "main");
        // 切替の直後に作業ツリー側の更新（RepositoryChanged 経由の読み直し）が重なる状況。
        await File.WriteAllTextAsync(Path.Combine(_root, "shared.txt"), "base" + "\n" + "edited" + "\n");
        await panel.RefreshCommand.ExecuteAsync(null);
        await panel.RefreshCommand.ExecuteAsync(null);

        var section = Assert.Single(panel.WorkingTreeSections);
        Assert.Contains("main", section.Name);
        Assert.Equal(
            new[] { "feature-only.txt", "shared.txt" },
            LeafPaths(section).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.False(panel.Capabilities.CanCommit);
    }

    [Fact]
    public async Task amendを立てたまま基準を切り替えてもコミットできない()
    {
        await CommitAsync("a.txt", "a\n", "first");

        var panel = CreatePanel();
        await panel.RefreshCommand.ExecuteAsync(null);
        panel.Amend = true;
        await panel.RefreshCommand.ExecuteAsync(null);
        panel.CommitMessage = "amend したい";
        Assert.True(panel.CommitCommand.CanExecute(null));

        SelectBase(GitCompareBaseKind.Branch, "main");
        await panel.RefreshCommand.ExecuteAsync(null);

        // コミットはインデックスを確定する操作。main との比較には存在しない。
        Assert.False(panel.CommitCommand.CanExecute(null));
        await panel.CommitCommand.ExecuteAsync(null);
        Assert.Equal("1", (await MustRunAsync("rev-list", "--count", "HEAD")).Output.Trim());
    }

    [Fact]
    public async Task 一覧の取得が失敗したら理由をパネルに出す()
    {
        await CommitAsync("a.txt", "a\n", "first");
        // ブランチ名と同名のディレクトリ。引数に -- が無いと git は曖昧として弾く（＝一覧が空になる）。
        await CommitAsync("docs/a.md", "x\n", "docs");
        await MustRunAsync("branch", "docs");
        await MustRunAsync("checkout", "-b", "feature");
        await CommitAsync("docs/a.md", "x\ny\n", "edit");

        var panel = CreatePanel();
        SelectBase(GitCompareBaseKind.Branch, "docs");
        await panel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("", panel.CompareBaseError);
        var section = Assert.Single(panel.WorkingTreeSections);
        Assert.Equal(new[] { "docs/a.md" }, LeafPaths(section).ToArray());
    }

    // ===== ヘルパー =====

    private GitPanelViewModel CreatePanel()
        => new(_git, new FakeEditorService(), _workspace,
            new GitRootSwitchViewModel(_git, _workspace), _compareBase);

    private DiffSessionViewModel CreateDiff()
        => new(_git, new FakeEditorService(), _workspace, new DiffFileGateway(),
            new DiffSessionQuery(_git), new DiffSessionCommandHandler(_git), new LoomoSettings(),
            _compareBase);

    private void SelectBase(GitCompareBaseKind kind, string branch)
    {
        _compareBase.SelectedBranch = branch;
        _compareBase.SelectedMode = _compareBase.ModeOptions.First(o => o.Kind == kind);
    }

    private static IEnumerable<string> LeafPaths(GitChangeTreeNode node)
        => node.Change is not null
            ? new[] { node.Change.Entry.Path }
            : node.Children.SelectMany(LeafPaths);

    private static async Task WaitAsync(Func<bool> condition)
    {
        // 読み直しは fire-and-forget なので待つしかない。全テスト並列実行では git のプロセス起動が
        // スレッドプール待ちに入るぶん遅くなるので、単体実行の体感より余裕を持たせる。
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        Assert.True(condition(),
            $"条件が満たされませんでした（{elapsed.Elapsed.TotalSeconds:F1}秒待機／更新が走っていない可能性）。");
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
