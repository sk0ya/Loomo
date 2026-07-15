using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ブランチ一覧→ツリー変換（<see cref="BranchTreeBuilder"/>）の検証。
/// リモートがあればローカル／リモートの見出しで分ける・見出しの中は3個未満フラット／3個以上で
/// "/" フォルダ化、入力順保持、リーフのメタ情報維持、絞り込みの平坦化を固定する。
/// </summary>
public sealed class BranchTreeBuilderTests
{
    private static GitBranchInfo Branch(string name, bool current = false, bool remote = false,
        string? upstream = null) => new(name, current, remote, upstream);

    /// <summary>見出し配下（リモートがあるとき）を引くヘルパ。</summary>
    private static BranchTreeNode Section(IReadOnlyList<BranchTreeNode> tree, string label) =>
        Assert.Single(tree, n => n.IsSection && n.Label == label);

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
    public void リモートがあればローカルとリモートの見出しに分かれる()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("main", current: true),
            Branch("origin/feature/login", remote: true),
            Branch("origin/main", remote: true),
        });

        Assert.Equal(new[] { "ローカル", "origin" }, tree.Select(n => n.Label));
        Assert.All(tree, n => Assert.True(n.IsSection));

        var local = Section(tree, "ローカル");
        Assert.Equal("main", Assert.Single(local.Children).Label);
        Assert.Equal(1, local.LeafCount);

        // リモート見出しの中はリモート名を剥がしたラベルになる（"origin" の中に "origin/" は要らない）
        var origin = Section(tree, "origin");
        Assert.Equal(new[] { "feature/login", "main" }, origin.Children.Select(n => n.Label));
        Assert.Equal(2, origin.LeafCount);
        // リーフはフルネームのブランチ情報を保持する（チェックアウトに使う）
        Assert.Equal("origin/feature/login", origin.Children[0].Branch!.Name);
        Assert.True(origin.Children[0].IsRemote);
    }

    [Fact]
    public void 見出しの中でも三個以上なら多段のフォルダになる()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("main"),
            Branch("origin/feature/login", remote: true),
            Branch("origin/feature/signup", remote: true),
            Branch("origin/main", remote: true),
        });

        var origin = Section(tree, "origin");
        var feature = origin.Children[0];
        Assert.True(feature.IsFolder);
        Assert.False(feature.IsSection);   // 見出しではなくただのフォルダ
        Assert.Equal("feature", feature.Label);
        Assert.Equal(new[] { "login", "signup" }, feature.Children.Select(c => c.Label));
        Assert.Equal("origin/feature/login", feature.Children[0].Branch!.Name);
        Assert.Equal("main", origin.Children[1].Label);
    }

    [Fact]
    public void 複数のリモートはそれぞれの見出しになる()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("main", current: true),
            Branch("origin/main", remote: true),
            Branch("upstream/main", remote: true),
        });

        Assert.Equal(new[] { "ローカル", "origin", "upstream" }, tree.Select(n => n.Label));
    }

    [Fact]
    public void ローカル見出しは開きリモート見出しは畳んでおく()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("main", current: true),
            Branch("origin/main", remote: true),
        });

        // 現在ブランチが見えないと切替が始まらない。逆にリモートは数が多く、開いていると一覧が埋まる
        Assert.True(Section(tree, "ローカル").IsExpanded);
        Assert.False(Section(tree, "origin").IsExpanded);
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
    public void 既定では現在ブランチへの経路だけ展開される()
    {
        // ローカルだけ（見出しなし）で、フォルダ化される 3 本
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("feature/login", current: true),
            Branch("fix/crash"),
            Branch("main"),
        });

        Assert.True(tree.Single(n => n.Label == "feature").IsExpanded);
        Assert.False(tree.Single(n => n.Label == "fix").IsExpanded);
    }

    [Fact]
    public void 構成が同じならUpdateは同一インスタンスを返しビューを壊さない()
    {
        var branches = new[]
        {
            Branch("feature/login"),
            Branch("feature/signup"),
            Branch("main", current: true),
        };
        var tree = BranchTreeBuilder.Build(branches);
        tree[0].IsExpanded = true;  // ユーザー操作相当

        // 同じ構成（別インスタンスの配列）での更新は no-op
        var updated = BranchTreeBuilder.Update(tree, new[]
        {
            Branch("feature/login"),
            Branch("feature/signup"),
            Branch("main", current: true),
        });
        Assert.Same(tree, updated);
    }

    [Fact]
    public void 構成が変わったら開閉状態を引き継いで作り直す()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("feature/login"),
            Branch("fix/crash"),
            Branch("main", current: true),
        });
        tree.Single(n => n.Label == "feature").IsExpanded = true;  // ユーザーが開いた

        // fix/crash へチェックアウトした後の構成
        var updated = BranchTreeBuilder.Update(tree, new[]
        {
            Branch("feature/login"),
            Branch("fix/crash", current: true),
            Branch("main"),
        });

        Assert.NotSame(tree, updated);
        // ユーザーが開いた feature はそのまま、新しい現在ブランチの経路 fix も展開
        Assert.True(updated.Single(n => n.Label == "feature").IsExpanded);
        Assert.True(updated.Single(n => n.Label == "fix").IsExpanded);
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

    // ===== 絞り込み =====

    [Fact]
    public void 絞り込み中は見出しもフォルダも挟まずフルネームで並べる()
    {
        var branches = new[]
        {
            Branch("main", current: true),
            Branch("feature/login"),
            Branch("feature/signup"),
            Branch("origin/feature/login", remote: true),
        };

        var tree = BranchTreeBuilder.BuildFiltered(branches, "login");

        Assert.Equal(new[] { "feature/login", "origin/feature/login" }, tree.Select(n => n.Label));
        Assert.All(tree, n => Assert.False(n.IsFolder));
    }

    [Fact]
    public void 絞り込みは大文字小文字を区別しない()
    {
        var tree = BranchTreeBuilder.BuildFiltered(new[] { Branch("Feature/Login") }, "login");
        Assert.Equal("Feature/Login", Assert.Single(tree).Label);
    }

    [Fact]
    public void 空語の絞り込みは通常のツリーと同じ()
    {
        var branches = new[]
        {
            Branch("main", current: true),
            Branch("origin/main", remote: true),
        };

        var tree = BranchTreeBuilder.BuildFiltered(branches, "  ");
        Assert.Equal(new[] { "ローカル", "origin" }, tree.Select(n => n.Label));
    }

    [Fact]
    public void 一致が無ければ空になる()
    {
        var tree = BranchTreeBuilder.BuildFiltered(new[] { Branch("main") }, "存在しない");
        Assert.Empty(tree);
    }

    // ===== 行の表示情報 =====

    [Fact]
    public void 上流との差はある方向だけを矢印で示す()
    {
        Assert.Equal("", Node(ahead: 0, behind: 0).TrackLabel);
        Assert.Equal("↑2", Node(ahead: 2, behind: 0).TrackLabel);
        Assert.Equal("↓3", Node(ahead: 0, behind: 3).TrackLabel);
        Assert.Equal("↑2 ↓3", Node(ahead: 2, behind: 3).TrackLabel);

        static BranchTreeNode Node(int ahead, int behind) => new()
        {
            Label = "main",
            Branch = new GitBranchInfo("main", true, false, "origin/main") { Ahead = ahead, Behind = behind },
        };
    }

    [Fact]
    public void フォルダには上流の差も日時も出さない()
    {
        var tree = BranchTreeBuilder.Build(new[]
        {
            Branch("feature/login"),
            Branch("feature/signup"),
            Branch("main"),
        });

        var feature = tree[0];
        Assert.Equal("", feature.TrackLabel);
        Assert.Equal("", feature.RelativeDate);
        Assert.False(feature.IsUpstreamGone);
    }

    [Fact]
    public void 相対日時は経過時間の粒度で丸める()
    {
        Assert.Equal("たった今", At(TimeSpan.FromSeconds(20)));
        Assert.Equal("5分前", At(TimeSpan.FromMinutes(5)));
        Assert.Equal("3時間前", At(TimeSpan.FromHours(3)));
        Assert.Equal("2日前", At(TimeSpan.FromDays(2)));
        Assert.Equal("2か月前", At(TimeSpan.FromDays(70)));
        Assert.Equal("1年前", At(TimeSpan.FromDays(400)));
        // 時計ずれ（未来のコミット）は何も出さない
        Assert.Equal("", At(TimeSpan.FromHours(-1)));

        static string At(TimeSpan ago) => new BranchTreeNode
        {
            Label = "main",
            Branch = new GitBranchInfo("main", true, false, null)
            {
                LastCommit = DateTimeOffset.Now - ago,
            },
        }.RelativeDate;
    }

    [Fact]
    public void 相対日時を持たせても構成が同じならUpdateは同一インスタンスを返す()
    {
        // 「2日前」を GitBranchInfo に持たせると時間経過だけでレコードが不一致になり、
        // 一覧が毎回作り直されてビュー状態が飛ぶ。日時は絶対値で持つ、を固定する
        var at = DateTimeOffset.Now.AddDays(-2);
        GitBranchInfo[] Snapshot() => new[]
        {
            new GitBranchInfo("main", true, false, "origin/main") { Ahead = 1, LastCommit = at },
            new GitBranchInfo("origin/main", false, true, null) { LastCommit = at },
        };

        var tree = BranchTreeBuilder.Build(Snapshot());
        Assert.Same(tree, BranchTreeBuilder.Update(tree, Snapshot()));
    }
}
