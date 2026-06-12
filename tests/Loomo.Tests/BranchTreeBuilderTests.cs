using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ブランチ一覧→ツリー変換（<see cref="BranchTreeBuilder"/>）の検証。
/// 3個未満はフラット・3個以上で "/" フォルダ化、入力順保持、リーフのメタ情報維持を固定する。
/// </summary>
public sealed class BranchTreeBuilderTests
{
    private static GitBranchInfo Branch(string name, bool current = false, bool remote = false,
        string? upstream = null) => new(name, current, remote, upstream);

    [Fact]
    public void 二個以下はスラッシュを含んでもフラットなまま()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("main", current: true),
            Branch("feature/login"),
        });

        Assert.Equal(2, tree.Count);
        Assert.All(tree, n => Assert.False(n.IsFolder));
        // フォルダ化しないのでラベルはフルネーム
        Assert.Equal("feature/login", tree[1].Label);
    }

    [Fact]
    public void 三個以上でスラッシュ区切りをフォルダ化する()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("feature/login"),
            Branch("feature/signup"),
            Branch("main", current: true),
        });

        Assert.Equal(2, tree.Count);

        var feature = tree[0];
        Assert.True(feature.IsFolder);
        Assert.Equal("feature", feature.Label);
        Assert.Equal(new[] { "login", "signup" }, feature.Children.Select(c => c.Label));

        var main = tree[1];
        Assert.False(main.IsFolder);
        Assert.True(main.IsCurrent);
    }

    [Fact]
    public void 多段のセグメントは入れ子のフォルダになる()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("main"),
            Branch("origin/feature/login", remote: true),
            Branch("origin/main", remote: true),
        });

        var origin = Assert.Single(tree, n => n.IsFolder);
        Assert.Equal("origin", origin.Label);

        var feature = origin.Children[0];
        Assert.True(feature.IsFolder);
        var login = Assert.Single(feature.Children);
        Assert.Equal("login", login.Label);
        Assert.True(login.IsRemote);
        // リーフはフルネームのブランチ情報を保持する（チェックアウトに使う）
        Assert.Equal("origin/feature/login", login.Branch!.Name);

        Assert.Equal("main", origin.Children[1].Label);
    }

    [Fact]
    public void 同じプレフィックスは一つのフォルダへまとまり入力順を保つ()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("fix/a"),
            Branch("main"),
            Branch("fix/b"),
        });

        // フォルダは初出位置（先頭）、main はその後
        Assert.Equal(new[] { "fix", "main" }, tree.Select(n => n.Label));
        Assert.Equal(new[] { "a", "b" }, tree[0].Children.Select(c => c.Label));
    }

    [Fact]
    public void フォルダと同名のブランチは別ノードとして共存する()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("release"),
            Branch("release/v1"),
            Branch("release/v2"),
        });

        Assert.Equal(2, tree.Count);
        Assert.Contains(tree, n => !n.IsFolder && n.Label == "release");
        var folder = Assert.Single(tree, n => n.IsFolder);
        Assert.Equal(2, folder.Children.Count);
    }

    [Fact]
    public void ツールチップはフルネームと上流を示しフォルダには出さない()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("feature/login", upstream: "origin/feature/login"),
            Branch("feature/signup"),
            Branch("main"),
        });

        var feature = tree[0];
        Assert.Null(feature.ToolTip);
        Assert.Equal("feature/login → origin/feature/login", feature.Children[0].ToolTip);
        Assert.Equal("feature/signup", feature.Children[1].ToolTip);
    }
}
