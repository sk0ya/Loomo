using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;
using sk0ya.Loomo.Services.GitHub;

namespace sk0ya.Loomo.Tests;

/// <summary>ブランチ一覧の行に、そのブランチから出ている PR を添える（§24.39）。</summary>
public sealed class BranchPullRequestsTests
{
    private static GitHubPullRequest Pr(int number, string head, bool fork = false, bool draft = false, string review = "")
        => new(number, $"PR {number}", "alice", head, "main", draft, null, $"https://github.com/o/r/pull/{number}",
            review, GitHubChecksState.None, fork);

    private static IEnumerable<BranchTreeNode> Leaves(IEnumerable<BranchTreeNode> nodes)
        => nodes.SelectMany(n => n.Branch is not null ? new[] { n } : Leaves(n.Children));

    [Fact]
    public void ローカルとリモートのブランチの両方に同じPRが付く()
    {
        var tree = BranchTreeBuilder.Build([
            new GitBranchInfo("feature/login", false, false, "origin/feature/login"),
            new GitBranchInfo("main", true, false, "origin/main"),
            new GitBranchInfo("origin/feature/login", false, true, null),
        ]);

        BranchPullRequests.Apply(tree, BranchPullRequests.ByHead([Pr(12, "feature/login")]));

        var leaves = Leaves(tree).ToList();
        Assert.All(leaves.Where(n => n.Branch!.Name.EndsWith("feature/login")), n => Assert.Equal("#12", n.PullRequestLabel));
        Assert.False(leaves.Single(n => n.Branch!.Name == "main").HasPullRequest);
    }

    [Fact]
    public void フォークのPRは名前が同じでも付けず_同じheadなら新しい方()
    {
        var byHead = BranchPullRequests.ByHead([Pr(3, "fix"), Pr(9, "fix"), Pr(20, "patch-1", fork: true)]);

        Assert.Equal(9, byHead["fix"].Number);
        Assert.False(byHead.ContainsKey("patch-1"));
    }

    [Fact]
    public void PRが閉じたら印は外れる()
    {
        var tree = BranchTreeBuilder.Build([new GitBranchInfo("fix", true, false, null)]);
        BranchPullRequests.Apply(tree, BranchPullRequests.ByHead([Pr(5, "fix")]));
        Assert.True(Leaves(tree).Single().HasPullRequest);

        BranchPullRequests.Apply(tree, BranchPullRequests.ByHead([]));

        Assert.False(Leaves(tree).Single().HasPullRequest);
    }

    [Theory]
    [InlineData(true, "", "下書き")]
    [InlineData(false, "APPROVED", "承認済み")]
    [InlineData(false, "CHANGES_REQUESTED", "変更を求められている")]
    [InlineData(false, "REVIEW_REQUIRED", "レビュー待ち")]
    [InlineData(false, "", "オープン")]
    public void 状態の言葉(bool draft, string review, string expected)
        => Assert.Equal(expected, BranchPullRequests.StateLabel(Pr(1, "x", draft: draft, review: review)));

    [Fact]
    public void リモート名を外して比べる()
    {
        Assert.Equal("feature/a", BranchPullRequests.HeadName("origin/feature/a", isRemote: true));
        Assert.Equal("origin/feature/a", BranchPullRequests.HeadName("origin/feature/a", isRemote: false));
    }
}
