using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>§24.70 差分で、移動しただけのブロックを見分ける。</summary>
public sealed class DiffMoveDetectorTests
{
    private static (DiffMoveSide, string) R(string text) => (DiffMoveSide.Removed, text);
    private static (DiffMoveSide, string) A(string text) => (DiffMoveSide.Added, text);
    private static (DiffMoveSide, string) C(string text) => (DiffMoveSide.None, text);

    private static readonly string[] Method =
    [
        "    private static int Compute(int value)",
        "    {",
        "        return value * Factor + Offset;",
        "    }",
    ];

    [Fact]
    public void A_method_moved_down_is_one_block_pointing_both_ways()
    {
        var rows = new List<(DiffMoveSide, string)>();
        rows.AddRange(Method.Select(R));               // 0..3 上で消えた
        rows.Add(C("    public void Run() { }"));       // 4
        rows.AddRange(Method.Select(A));               // 5..8 下に足された

        var moves = DiffMoveDetector.Detect(rows);

        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(new DiffMove(0, 5, true, 4), moves[i]));
        Assert.All(Enumerable.Range(5, 4), i => Assert.Equal(new DiffMove(0, 0, false, 4), moves[i]));
        Assert.Null(moves[4]);
    }

    [Fact]
    public void Indentation_does_not_matter()
    {
        var rows = new List<(DiffMoveSide, string)>();
        rows.AddRange(Method.Select(R));
        rows.Add(C("}"));
        rows.AddRange(Method.Select(l => A("    " + l)));   // クラスの中へ入れた（字下げが 1 段深い）

        var moves = DiffMoveDetector.Detect(rows);

        Assert.NotNull(moves[0]);
        Assert.NotNull(moves[5]);
    }

    [Fact]
    public void Reindenting_in_place_is_not_a_move()
    {
        // 同じかたまりの中で消して足した＝その場で字下げを直しただけ
        var rows = Method.Select(R).Concat(Method.Select(l => A("  " + l))).ToList();
        Assert.All(DiffMoveDetector.Detect(rows), m => Assert.Null(m));
    }

    [Fact]
    public void Short_or_thin_blocks_are_not_moves()
    {
        var twoLines = new[] { R("var total = items.Sum(i => i.Price);"), R("return total;"), C("x"),
                               A("var total = items.Sum(i => i.Price);"), A("return total;") };
        Assert.All(DiffMoveDetector.Detect(twoLines), m => Assert.Null(m));

        var braces = new[] { R("}"), R("}"), R("});"), R(""), C("x"), A("}"), A("}"), A("});"), A("") };
        Assert.All(DiffMoveDetector.Detect(braces), m => Assert.Null(m));
    }

    [Fact]
    public void Edited_lines_end_the_block()
    {
        var rows = new List<(DiffMoveSide, string)>();
        rows.AddRange(Method.Select(R));
        rows.Add(C("// 区切り"));
        rows.AddRange(Method.Take(3).Select(A));
        rows.Add(A("    } // 直した"));

        var moves = DiffMoveDetector.Detect(rows);

        Assert.Equal(3, moves[0]!.Length);
        Assert.Null(moves[3]);
        Assert.Null(moves[8]);
    }

    [Fact]
    public void Unified_rows_strip_the_patch_prefix_and_drop_inline_spans_of_moved_rows()
    {
        var rows = new List<DiffRowVm>();
        rows.AddRange(Method.Select(l => new DiffRowVm("Removed", "-" + l) { Prefixed = true }));
        rows.Add(new DiffRowVm("Context", "     public void Run() { }") { Prefixed = true });
        rows.AddRange(Method.Select(l => new DiffRowVm("Added", "+" + l) { Prefixed = true }));

        var moves = DiffMoveHighlighter.ForUnified(rows);
        Assert.Equal(9, moves.Count);
        Assert.True(moves[0]!.IsSource);
        Assert.False(moves[5]!.IsSource);

        IReadOnlyList<TextSpan>? span = [new TextSpan(1, 2)];
        var inline = Enumerable.Repeat(span, 9).ToList();
        var kept = DiffMoveHighlighter.WithoutMovedInline(inline, moves);
        Assert.Null(kept[0]);
        Assert.NotNull(kept[4]);
    }

    [Fact]
    public void Adhoc_rows_are_compared_as_they_are()
    {
        // アドホック比較の行は本文そのもの（頭の「-」も本文）
        var lines = new[] { "- item one is long enough", "- item two is long enough", "- item three is long" };
        var rows = lines.Select(l => new DiffRowVm("Removed", l))
            .Append(new DiffRowVm("Context", "x"))
            .Concat(lines.Select(l => new DiffRowVm("Added", l))).ToList();

        Assert.NotNull(DiffMoveHighlighter.ForUnified(rows)[0]);
    }
}
