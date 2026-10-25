using System.Text;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ペインの配置を回す・裏返す・縦横を入れ替える・中身を巡回させる（§24.84）。</summary>
public sealed class PaneLayoutTransformsTests
{
    private static PaneLeaf Leaf(PaneKind kind, double weight = 1, bool hidden = false)
        => new() { Kind = kind, Weight = weight, Hidden = hidden };

    private static PaneSplit Split(SplitKind orientation, params PaneNode[] children)
    {
        var split = new PaneSplit { Orientation = orientation };
        split.Children.AddRange(children);
        return split;
    }

    /// <summary>C(左→右) / R(上→下) で木を一行に書く。</summary>
    private static string Shape(PaneNode node)
    {
        if (node is PaneLeaf leaf)
            return leaf.Kind.ToString();
        var split = (PaneSplit)node;
        var sb = new StringBuilder(split.Orientation == SplitKind.Columns ? "C(" : "R(");
        sb.Append(string.Join(",", split.Children.Select(Shape)));
        return sb.Append(')').ToString();
    }

    // Editor | (Terminal over Git)
    private static PaneNode Sample() => Split(SplitKind.Columns,
        Leaf(PaneKind.Editor, 2),
        Split(SplitKind.Rows, Leaf(PaneKind.Terminal), Leaf(PaneKind.Git)));

    [Fact]
    public void 時計回りに回すと左右は上下に_上下は右から左になる()
    {
        Assert.Equal("R(Editor,C(Git,Terminal))", Shape(PaneLayoutTransforms.RotateClockwise(Sample())));
    }

    [Fact]
    public void 反時計回りは時計回りの逆()
    {
        var back = PaneLayoutTransforms.RotateAnticlockwise(PaneLayoutTransforms.RotateClockwise(Sample()));

        Assert.Equal(Shape(Sample()), Shape(back));
        Assert.Equal("R(C(Terminal,Git),Editor)", Shape(PaneLayoutTransforms.RotateAnticlockwise(Sample())));
    }

    [Fact]
    public void 四回回すと元に戻る()
    {
        var node = Sample();
        for (var i = 0; i < 4; i++)
            node = PaneLayoutTransforms.RotateClockwise(node);

        Assert.Equal(Shape(Sample()), Shape(node));
    }

    [Fact]
    public void 裏返しはその向きの並びだけを逆にする()
    {
        Assert.Equal("C(R(Terminal,Git),Editor)", Shape(PaneLayoutTransforms.FlipLeftRight(Sample())));
        Assert.Equal("C(Editor,R(Git,Terminal))", Shape(PaneLayoutTransforms.FlipTopDown(Sample())));
    }

    [Fact]
    public void 縦横の入れ替えは順を保ったまま向きだけ変える()
    {
        Assert.Equal("R(Editor,C(Terminal,Git))", Shape(PaneLayoutTransforms.Transpose(Sample())));
    }

    [Fact]
    public void 比率と非表示の印は持ち越す()
    {
        var root = Split(SplitKind.Columns, Leaf(PaneKind.Editor, 3), Leaf(PaneKind.Browser, 1, hidden: true));

        var rotated = (PaneSplit)PaneLayoutTransforms.RotateClockwise(root);

        var editor = Assert.IsType<PaneLeaf>(rotated.Children[0]);
        Assert.Equal(3, editor.Weight);
        Assert.True(Assert.IsType<PaneLeaf>(rotated.Children[1]).Hidden);
    }

    [Fact]
    public void 中身の巡回は枠と比率を残して見えているペインだけを送る()
    {
        var root = Split(SplitKind.Columns,
            Leaf(PaneKind.Editor, 2), Leaf(PaneKind.Browser, 1, hidden: true), Leaf(PaneKind.Terminal, 1), Leaf(PaneKind.Git, 1));

        var cycled = (PaneSplit)PaneLayoutTransforms.CyclePanes(root);

        Assert.Equal("C(Git,Browser,Editor,Terminal)", Shape(cycled));
        Assert.Equal(2, cycled.Children[0].Weight);   // 枠の比率はその枠に残る
        Assert.True(((PaneLeaf)cycled.Children[1]).Hidden);
        Assert.Equal(Shape(root), Shape(PaneLayoutTransforms.CyclePanes(cycled, forward: false)));
    }

    [Fact]
    public void ペインが一つなら何も変わらない()
    {
        var leaf = Leaf(PaneKind.Editor);

        Assert.Same(leaf, PaneLayoutTransforms.RotateClockwise(leaf));
        Assert.Same(leaf, PaneLayoutTransforms.CyclePanes(leaf));
    }
}
