using System.IO;
using System.Windows;
using System.Windows.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 左列のツリーの右クリックメニュー（§24.17.3）を、実際にビューを組んで確かめる。1つの TreeView に付けられる
/// メニューは1つなので、行の種類で項目を出し分け、ワークツリーの項目はブランチの行では「ワークツリー」の
/// 下位メニューに、ブランチを持たないワークツリーの行では最上段へ持ち上げる。
/// </summary>
[Collection(WpfViewTests.Name)]
public sealed class GitPaneTreeMenuViewTests
{
    private readonly WpfViewHost _host;

    public GitPaneTreeMenuViewTests(WpfViewHost host) => _host = host;

    private static readonly GitWorktreeInfo Linked = new() { Path = @"C:\work\app.worktrees\feature", Branch = "feature" };

    [Fact]
    public void ブランチの行ではワークツリーを下位メニューにまとめタグの項目は出さない()
    {
        _host.Run(() =>
        {
            var view = CreateView();
            var node = new BranchTreeNode { Label = "feature", Branch = new GitBranchInfo("feature", false, false, null) };
            node.Worktree = Linked;

            Assert.True(view.PrepareTreeMenu(node));

            var menu = TreeMenu(view);
            var worktree = (MenuItem)view.FindName("BranchMenuWorktree")!;
            Assert.Equal(Visibility.Visible, worktree.Visibility);
            Assert.Contains("feature", (string)worktree.Header);
            Assert.Contains(worktree.Items.OfType<MenuItem>(), item => (string)item.Header == "ワークスペースとして開く");
            Assert.All(TopLevel(menu, GitTreeMenuKind.Tag), item => Assert.Equal(Visibility.Collapsed, item.Visibility));
            // 既に別の場所に置いているブランチを、もう一度「別の場所に置く」ことはできない（git が拒む）
            Assert.Equal(Visibility.Collapsed, ((MenuItem)view.FindName("BranchMenuCreateWorktree")!).Visibility);
        });
    }

    [Fact]
    public void ブランチを持たないワークツリーの行では項目を最上段へ持ち上げ戻せる()
    {
        _host.Run(() =>
        {
            var view = CreateView();
            var menu = TreeMenu(view);
            var worktree = (MenuItem)view.FindName("BranchMenuWorktree")!;
            var detached = new BranchTreeNode { Label = "(detached)", IsWorktreeItem = true };
            detached.Worktree = Linked with { Branch = null, IsDetached = true };

            Assert.True(view.PrepareTreeMenu(detached));
            Assert.Empty(worktree.Items);
            Assert.Equal(Visibility.Collapsed, worktree.Visibility);
            var hoisted = TopLevel(menu, GitTreeMenuKind.Worktree).ToList();
            Assert.NotEmpty(hoisted);
            // ロック解除・繋ぎ直しは、ロック中／フォルダーが消えたものにだけ出す（この行はどちらでもない）
            Assert.All(hoisted.Where(item => item.Name is not ("WorktreeMenuUnlock" or "WorktreeMenuRepair")),
                item => Assert.Equal(Visibility.Visible, item.Visibility));
            Assert.Equal(Visibility.Collapsed, ((MenuItem)view.FindName("WorktreeMenuUnlock")!).Visibility);
            Assert.All(TopLevel(menu, null), item => Assert.Equal(Visibility.Collapsed, item.Visibility));

            // 次にブランチの行を開いたら、下位メニューへ戻っている
            var branch = new BranchTreeNode { Label = "feature", Branch = new GitBranchInfo("feature", false, false, null) };
            branch.Worktree = Linked;
            Assert.True(view.PrepareTreeMenu(branch));
            Assert.Equal(hoisted.Count, worktree.Items.Count);
            Assert.Empty(TopLevel(menu, GitTreeMenuKind.Worktree));
        });
    }

    [Fact]
    public void フォルダや見出しではメニューを出さない()
    {
        _host.Run(() =>
        {
            var view = CreateView();
            Assert.False(view.PrepareTreeMenu(new BranchTreeNode { Label = "タグ", IsSection = true }));
        });
    }

    private static ContextMenu TreeMenu(GitSessionView view) => ((TreeView)view.FindName("BranchList")!).ContextMenu!;

    private static IEnumerable<FrameworkElement> TopLevel(ContextMenu menu, string? tag)
        => menu.Items.OfType<FrameworkElement>().Where(item => (item.Tag as string) == tag);

    private static GitSessionView CreateView()
    {
        var root = Path.Combine(Path.GetTempPath(), "loomo-git-tree-menu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new FakeWorkspaceService();
        workspace.OpenFolder(root);
        var git = new GitService(workspace);
        var query = new GitSessionQuery(git);
        var vm = new GitSessionViewModel(git, new FakeEditorService(), query,
            new GitSessionCommandHandler(git), new GitHistoryViewModel(query),
            new GitRepositoryStripViewModel(git, workspace));
        return new GitSessionView { DataContext = vm };
    }
}
