using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コミット一覧の参照バッジの素になる <c>%D</c>（<c>--decorate=full</c>）の解釈。
/// ブランチ・リモート・タグを色で分けるので、取り違えるとそのまま嘘の色になる。
/// </summary>
public sealed class GitRefLabelTests
{
    [Fact]
    public void HEADが指している枝はその枝の印として付く()
    {
        // "HEAD -> main" は参照が2つあるのではなく、main が「いま居る枝」であるという意味。
        var labels = GitRefLabels.Parse("HEAD -> refs/heads/main, refs/remotes/origin/main");

        Assert.Collection(labels,
            head =>
            {
                Assert.Equal(GitRefKind.LocalBranch, head.Kind);
                Assert.Equal("main", head.Name);
                Assert.True(head.IsHead);
            },
            remote =>
            {
                Assert.Equal(GitRefKind.RemoteBranch, remote.Kind);
                Assert.Equal("origin/main", remote.Name);
                Assert.False(remote.IsHead);
            });
    }

    [Fact]
    public void タグは種類の前置を剥がして名前だけにする()
    {
        // 実機の綴り（git は --decorate=full でもタグにだけ "tag: " を前置する）。
        // 剥がさないと種類不明の灰色になり、名前も生の完全名のまま出る。
        var labels = GitRefLabels.Parse(
            "HEAD -> refs/heads/main, tag: refs/tags/v2.0, tag: refs/tags/v1.0, refs/remotes/origin/main");

        Assert.Equal(
            new[] { GitRefKind.LocalBranch, GitRefKind.Tag, GitRefKind.Tag, GitRefKind.RemoteBranch },
            labels.Select(l => l.Kind));
        Assert.Equal(new[] { "main", "v2.0", "v1.0", "origin/main" }, labels.Select(l => l.Name));
    }

    [Fact]
    public void デタッチHEADはHEADそのものを出す()
    {
        var labels = GitRefLabels.Parse("HEAD, tag: refs/tags/v1.0");

        Assert.Equal(GitRefKind.Head, labels[0].Kind);
        Assert.Equal("HEAD", labels[0].Name);
        Assert.True(labels[0].IsHead);
        Assert.Equal(GitRefKind.Tag, labels[1].Kind);
        Assert.Equal("v1.0", labels[1].Name);
    }

    [Fact]
    public void リモート名と同じ名前のローカルブランチを取り違えない()
    {
        // 短縮名（"origin/main"）だけでは、リモートの main なのか
        // "origin/main" という名前のローカルブランチなのかを当てられない。完全名で受ける理由。
        var labels = GitRefLabels.Parse("refs/heads/origin/main");

        Assert.Equal(GitRefKind.LocalBranch, Assert.Single(labels).Kind);
        Assert.Equal("origin/main", labels[0].Name);
    }

    [Fact]
    public void 枝でもタグでもない参照は種類不明として名前だけ出す()
    {
        var labels = GitRefLabels.Parse("refs/stash");

        Assert.Equal(GitRefKind.Other, Assert.Single(labels).Kind);
        Assert.Equal("stash", labels[0].Name);
    }

    [Fact]
    public void 短縮名で来ても落ちず推測もしない()
    {
        // 装飾の設定が変わった場合の保険。色を付けずに名前だけ出す（推測して間違えるより良い）。
        var labels = GitRefLabels.Parse("HEAD -> main, origin/main");

        Assert.Equal(new[] { "main", "origin/main" }, labels.Select(l => l.Name));
        Assert.All(labels, l => Assert.Equal(GitRefKind.Other, l.Kind));
        Assert.True(labels[0].IsHead);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 参照が無い行は空(string? refs)
    {
        Assert.Empty(GitRefLabels.Parse(refs));
    }

    [Fact]
    public void ツールチップの参照は短縮名で出す()
    {
        // 生の %D は refs/remotes/origin/main のような完全名で、読ませるものではない。
        var row = new GitLogRow("*", "abc", "abc", "Loomo", "2026-09-19 10:00",
            "HEAD -> refs/heads/main, refs/remotes/origin/main", "直近の修正");

        Assert.Equal("直近の修正\nmain, origin/main", row.ToolTipText);
    }
}
