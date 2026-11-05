using System.IO;
using System.Windows.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Git ペイン左列の1本のツリー（§24.17.3）：ブランチの後ろにワークツリー（ブランチを持たないもの）・タグ・
/// サブモジュールの見出しを足し、ブランチの行にはそれを置いているワークツリーの印を付ける。
/// 固定したいのは「同じ中身なら同じインスタンス」——RefreshAsync はファイル保存でも走り、作り直すと
/// TreeView の選択と開きかけのメニューが落ちる。
/// </summary>
public sealed class GitPaneTreeComposerTests
{
    private static readonly GitBranchInfo Main = new("main", true, false, "origin/main");
    private static readonly GitBranchInfo Feature = new("feature", false, false, null);
    private static readonly GitBranchInfo[] Branches = [Main, Feature];

    private static GitWorktreeInfo Here => new() { Path = @"C:\work\app", Branch = "main", IsMain = true, IsCurrent = true };
    private static GitWorktreeInfo FeatureTree(int? changes = null)
        => new() { Path = @"C:\work\app.worktrees\feature", Branch = "feature", ChangeCount = changes };
    private static GitWorktreeInfo Detached
        => new() { Path = @"C:\work\app.worktrees\probe", Head = "abcdef1234", IsDetached = true };

    private static readonly GitTagInfo V1 = new("v1.0", "abc1234", "最初の版", true, null);

    [Fact]
    public void 足すものが無ければブランチのツリーそのもの()
    {
        var tree = BranchTreeBuilder.Build(Branches);
        var composed = GitPaneTreeComposer.Compose([], tree, null, Branches, [Here, FeatureTree()], [], []);
        Assert.Same(tree, composed);
    }

    [Fact]
    public void タグとサブモジュールは畳んだ見出しで後ろに足す()
    {
        var tree = BranchTreeBuilder.Build(Branches);
        var sub = new GitSubmoduleInfo("lib/x", "1234567890", null, true, false, false);
        var composed = GitPaneTreeComposer.Compose([], tree, null, Branches, [Here], [V1], [sub]);

        Assert.Equal(tree.Count + 2, composed.Count);
        var tags = composed[^2];
        Assert.True(tags.IsSection);
        Assert.Equal(GitPaneTreeComposer.TagSectionLabel, tags.Label);
        Assert.False(tags.IsExpanded);
        Assert.Same(V1, tags.Children.Single().Tag);
        Assert.False(tags.Children.Single().IsFolder);
        Assert.Equal("未初期化", composed[^1].Children.Single().Detail);
    }

    [Fact]
    public void ブランチを持たないワークツリーだけを見出しに出し既定で開いておく()
    {
        var tree = BranchTreeBuilder.Build(Branches);
        var composed = GitPaneTreeComposer.Compose([], tree, null, Branches, [Here, FeatureTree(), Detached], [], []);

        var section = composed[^1];
        Assert.Equal(GitPaneTreeComposer.WorktreeSectionLabel, section.Label);
        Assert.True(section.IsExpanded);
        var row = Assert.Single(section.Children);
        Assert.True(row.IsWorktreeItem);
        Assert.Equal(Detached.Path, row.Worktree!.Path);
    }

    [Fact]
    public void 同じ中身なら前回のインスタンスを返す()
    {
        var tree = BranchTreeBuilder.Build(Branches);
        var first = GitPaneTreeComposer.Compose([], tree, null, Branches, [Here, Detached], [V1], []);
        var second = GitPaneTreeComposer.Compose(first, tree, null, Branches,
            [Here, Detached], [new GitTagInfo("v1.0", "abc1234", "最初の版", true, null)], []);
        Assert.Same(first, second);
    }

    [Fact]
    public void ワークツリーの件数が変わっても行は作り直さず中身だけ差し替える()
    {
        var tree = BranchTreeBuilder.Build(Branches);
        var detached = Detached;
        var first = GitPaneTreeComposer.Compose([], tree, null, Branches, [Here, detached], [], []);
        var row = first[^1].Children.Single();

        var dirty = detached with { ChangeCount = 4 };
        var second = GitPaneTreeComposer.Compose(first, tree, null, Branches, [Here, dirty], [], []);

        Assert.Same(first, second);
        Assert.Same(row, second[^1].Children.Single());
        Assert.Equal("±4", row.WorktreeMark);
    }

    [Fact]
    public void 見出しを作り直しても開閉は引き継ぐ()
    {
        var tree = BranchTreeBuilder.Build(Branches);
        var first = GitPaneTreeComposer.Compose([], tree, null, Branches, [Here], [V1], []);
        first[^1].IsExpanded = true;

        var second = GitPaneTreeComposer.Compose(first, tree, null, Branches, [Here],
            [V1, new GitTagInfo("v2.0", "def5678", null, false, null)], []);

        Assert.NotSame(first, second);
        Assert.True(second[^1].IsExpanded);
        Assert.Equal(2, second[^1].Children.Count);
    }

    [Fact]
    public void 絞り込み中は一致した項目を見出しなしで平らに並べる()
    {
        var filtered = BranchTreeBuilder.BuildFiltered(Branches, "v1");
        var composed = GitPaneTreeComposer.Compose([], filtered, "v1", Branches,
            [Here, FeatureTree()], [V1, new GitTagInfo("v2.0", "def5678", null, false, null)], []);

        var only = Assert.Single(composed);
        Assert.Same(V1, only.Tag);
    }

    [Fact]
    public void 絞り込みで外れただけのブランチのワークツリーをブランチなし側へ出さない()
    {
        // "feature" は絞り込みに一致しないが、ブランチを持つワークツリーなので「ワークツリー」の見出しには来ない。
        var filtered = BranchTreeBuilder.BuildFiltered(Branches, "feat-x");
        var composed = GitPaneTreeComposer.Compose([], filtered, "app", Branches, [Here, FeatureTree()], [], []);
        Assert.Empty(composed);
    }

    [Fact]
    public void 別の場所に置いているブランチの行にだけ印を付ける()
    {
        var tree = BranchTreeBuilder.Build([.. Branches, new GitBranchInfo("origin/feature", false, true, null)]);
        var marks = new Dictionary<string, string> { [FeatureTree().Path] = "◐" };
        GitPaneTreeComposer.ApplyWorktreeMarks(tree, [Here, FeatureTree(changes: 2)], marks);

        var leaves = Leaves(tree).ToList();
        var main = leaves.Single(n => n.Branch == Main);
        var feature = leaves.Single(n => n.Branch == Feature);
        var remote = leaves.Single(n => n.Branch!.IsRemote);

        Assert.False(main.HasWorktree);          // いまの場所＝現在ブランチの ● で足りる
        Assert.True(feature.HasWorktree);
        Assert.Equal("📁 ±2", feature.WorktreeMark);
        Assert.Equal("◐", feature.AgentMark);
        Assert.False(remote.HasWorktree);

        // 片付けたら印も外れる
        GitPaneTreeComposer.ApplyWorktreeMarks(tree, [Here], new Dictionary<string, string>());
        Assert.False(feature.HasWorktree);
        Assert.Equal("", feature.AgentMark);
    }

    [Theory]
    [InlineData(null, GitTreeMenuKind.Branch, false, true)]
    [InlineData(null, GitTreeMenuKind.Tag, false, false)]
    [InlineData(GitTreeMenuKind.BranchWorktree, GitTreeMenuKind.Branch, false, false)]
    [InlineData(GitTreeMenuKind.BranchWorktree, GitTreeMenuKind.Branch, true, true)]
    [InlineData(GitTreeMenuKind.Worktree, GitTreeMenuKind.Worktree, false, true)]
    [InlineData(GitTreeMenuKind.Tag, GitTreeMenuKind.Submodule, false, false)]
    [InlineData(GitTreeMenuKind.Submodule, GitTreeMenuKind.Submodule, false, true)]
    public void 右クリックメニューは行の種類の項目だけ出す(string? itemTag, string kind, bool hasWorktree, bool visible)
        => Assert.Equal(visible, GitTreeMenuKind.IsVisible(itemTag, kind, hasWorktree));

    [Fact]
    public void コミット一覧のF5は読み直し()
        => Assert.Equal(GitLogKeyAction.Reload,
            GitSessionView.ResolveLogKey(Key.F5, ModifierKeys.None, pendingG: false, hasFilters: false).Action);

    [Fact]
    public void 空の絞り込み欄のEscは帯を畳む()
    {
        Assert.Equal(GitLogFilterKeyCommand.Close, GitSessionKeyboardMapper.ResolveLogFilterKey(Key.Escape, ""));
        Assert.Equal(GitLogFilterKeyCommand.Clear, GitSessionKeyboardMapper.ResolveLogFilterKey(Key.Escape, "x"));
        Assert.Equal(GitLogFilterKeyCommand.FocusList, GitSessionKeyboardMapper.ResolveLogFilterKey(Key.Enter, "x"));
    }

    [Fact]
    public void 絞り込み帯は効いている間は畳まれず全解除で畳む()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomo-git-filter-bar", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(root);
        var history = new GitHistoryViewModel(new GitSessionQuery(new GitService(workspace)));

        Assert.False(history.IsLogFilterBarVisible);   // ふだんは畳んでおく
        history.DateFrom = new DateTime(2026, 1, 1);
        Assert.True(history.IsLogFilterBarVisible);    // 開いていなくても、効いている間は出す

        history.IsLogFilterBarOpen = true;
        history.ClearLogFiltersCommand.Execute(null);
        Assert.False(history.IsLogFilterBarOpen);
        Assert.False(history.IsLogFilterBarVisible);
    }

    private static IEnumerable<BranchTreeNode> Leaves(IEnumerable<BranchTreeNode> nodes)
        => nodes.SelectMany(n => n.Branch is not null ? [n] : Leaves(n.Children));
}
