using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Core.Diff;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 行内差分（<see cref="InlineDiff"/>）と、書き換えのかたまりでの行の組み合わせ（<see cref="LinePairing"/>）の検証。
/// </summary>
public class InlineDiffTests
{
    private static string[] Marked(string text, IReadOnlyList<TextSpan> spans)
        => spans.Select(s => text.Substring(s.Start, s.Length)).ToArray();

    [Fact]
    public void Identical_lines_have_no_inline_change()
        => Assert.Null(InlineDiff.Compute("same", "same"));

    [Fact]
    public void Changed_word_is_marked_on_both_sides()
    {
        var change = InlineDiff.Compute("int count = 1;", "int count = 2;");
        Assert.NotNull(change);
        Assert.Equal(new[] { "1" }, Marked("int count = 1;", change!.Left));
        Assert.Equal(new[] { "2" }, Marked("int count = 2;", change.Right));
    }

    [Fact]
    public void Small_edit_inside_a_word_is_narrowed_to_the_characters()
    {
        // 字句単位だと語ごと塗られる——共通の先頭・末尾を剥がして「s」だけにする。
        var change = InlineDiff.Compute("ClearDiagnostics(e);", "ClearDiagnostic(e);");
        Assert.NotNull(change);
        Assert.Equal(new[] { "s" }, Marked("ClearDiagnostics(e);", change!.Left));
        Assert.Empty(change.Right);
    }

    [Fact]
    public void Inserted_argument_is_marked_only_on_the_right()
    {
        const string left = "Call(a);";
        const string right = "Call(a, b);";
        var change = InlineDiff.Compute(left, right);
        Assert.NotNull(change);
        Assert.Empty(change!.Left);
        Assert.Equal(new[] { ", b" }, Marked(right, change.Right));
    }

    [Fact]
    public void Adjacent_changes_separated_by_a_space_are_merged()
    {
        // 「a→x」「b→y」の間の空白1つだけ塗り残すと縞模様になる。
        const string left = "let a b = 0";
        const string right = "let x y = 0";
        var change = InlineDiff.Compute(left, right);
        Assert.NotNull(change);
        Assert.Equal(new[] { "a b" }, Marked(left, change!.Left));
        Assert.Equal(new[] { "x y" }, Marked(right, change.Right));
    }

    [Fact]
    public void Unrelated_lines_have_no_inline_change()
    {
        // 全体が塗られた行は背景色以上のことを言わない。
        Assert.Null(InlineDiff.Compute("return value;", "foreach (var item in list)"));
    }

    [Fact]
    public void Japanese_text_is_compared_character_by_character()
    {
        const string left = "変更を保存します";
        const string right = "変更を破棄します";
        var change = InlineDiff.Compute(left, right);
        Assert.NotNull(change);
        Assert.Equal(new[] { "保存" }, Marked(left, change!.Left));
        Assert.Equal(new[] { "破棄" }, Marked(right, change.Right));
    }

    [Fact]
    public void Whitespace_only_change_is_marked()
    {
        var change = InlineDiff.Compute("a = b", "a  = b");
        Assert.NotNull(change);
        // 共通の空白1つを剥がした残り（足された空白1つ）だけが右に付く。
        Assert.Empty(change!.Left);
        Assert.Equal(new[] { " " }, Marked("a  = b", change.Right));
    }

    [Fact]
    public void Surrogate_pairs_are_never_split()
    {
        const string left = "x 😀 y";
        const string right = "x 😁 y";
        var change = InlineDiff.Compute(left, right);
        Assert.NotNull(change);
        Assert.Equal(new[] { "😀" }, Marked(left, change!.Left));
        Assert.Equal(new[] { "😁" }, Marked(right, change.Right));
    }

    [Fact]
    public void Random_spans_stay_in_bounds_ordered_and_cover_only_changed_text()
    {
        // 塗る範囲が行の外へ出ない・重ならない・昇順。範囲の外を両側から取り出すと、同じ並びになる
        // （塗られていない部分は本当に共通）——これが崩れると行内の色が嘘をつく。
        var rng = new Random(31);
        var pieces = new[] { "foo", "bar", " ", "(", ")", "=", "1", "2", "x", "日本", ";", "  " };
        for (var trial = 0; trial < 3000; trial++)
        {
            string RandomLine() => string.Concat(Enumerable.Range(0, rng.Next(1, 10)).Select(_ => pieces[rng.Next(pieces.Length)]));
            var left = RandomLine();
            var right = rng.Next(3) == 0 ? RandomLine() : Mutate(rng, left, pieces);
            var change = InlineDiff.Compute(left, right);
            if (change is null) continue;

            AssertWellFormed(left, change.Left);
            AssertWellFormed(right, change.Right);
            Assert.Equal(Unmarked(left, change.Left), Unmarked(right, change.Right));
        }
    }

    private static string Mutate(Random rng, string line, string[] pieces)
    {
        var at = rng.Next(line.Length + 1);
        return rng.Next(3) switch
        {
            0 => line.Insert(at, pieces[rng.Next(pieces.Length)]),
            1 when line.Length > 0 => line.Remove(Math.Min(at, line.Length - 1), 1),
            _ => line + pieces[rng.Next(pieces.Length)],
        };
    }

    private static void AssertWellFormed(string text, IReadOnlyList<TextSpan> spans)
    {
        var previousEnd = 0;
        foreach (var span in spans)
        {
            Assert.True(span.Length > 0);
            Assert.True(span.Start >= previousEnd, "spans must be ordered and disjoint");
            Assert.True(span.End <= text.Length);
            previousEnd = span.End;
        }
    }

    private static string Unmarked(string text, IReadOnlyList<TextSpan> spans)
    {
        var parts = new List<string>();
        var at = 0;
        foreach (var span in spans)
        {
            parts.Add(text[at..span.Start]);
            at = span.End;
        }
        parts.Add(text[at..]);
        return string.Join("\u0001", parts.Where(p => p.Length > 0)).Replace("\u0001", "");
    }

    // ===== 行の組み合わせ =====

    [Fact]
    public void Similar_lines_are_paired_even_when_an_extra_line_is_inserted_before_them()
    {
        // 旧実装は i 番目どうしを並べたので「int count = 1;」が新しい1行目（無関係）と並んでいた。
        var removed = new[] { "int count = 1;" };
        var added = new[] { "// 件数を数える", "int count = 2;" };
        var rows = LinePairing.Pair(removed, added);
        Assert.Equal(new[] { (-1, 0), (0, 1) }, rows);
    }

    [Fact]
    public void Pairing_keeps_order_and_zips_leftovers_between_pairs()
    {
        var removed = new[] { "alpha();", "Beta(1);", "gamma();" };
        var added = new[] { "Beta(2);", "zzz", "yyy" };
        var rows = LinePairing.Pair(removed, added);
        // alpha は対が無い → 先に単独、Beta どうしを並べ、残り（gamma と zzz/yyy）は上から詰める。
        Assert.Equal(new[] { (0, -1), (1, 0), (2, 1), (-1, 2) }, rows);
    }

    [Fact]
    public void Dissimilar_blocks_fall_back_to_zipping()
    {
        var rows = LinePairing.Pair(new[] { "aaa", "bbb" }, new[] { "xxx", "yyy", "zzz" });
        Assert.Equal(new[] { (0, 0), (1, 1), (-1, 2) }, rows);
    }

    [Fact]
    public void Single_line_rewrite_is_always_paired()
        => Assert.Equal(new[] { (0, 0) }, LinePairing.Pair(new[] { "aaa" }, new[] { "zzz" }));

    [Fact]
    public void Random_pairings_use_every_line_once_in_order()
    {
        var rng = new Random(5);
        var vocabulary = new[] { "a(1);", "a(2);", "b = c;", "b = d;", "return x;", "", "}" };
        for (var trial = 0; trial < 2000; trial++)
        {
            var removed = Enumerable.Range(0, rng.Next(8)).Select(_ => vocabulary[rng.Next(vocabulary.Length)]).ToArray();
            var added = Enumerable.Range(0, rng.Next(8)).Select(_ => vocabulary[rng.Next(vocabulary.Length)]).ToArray();
            var rows = LinePairing.Pair(removed, added);
            Assert.Equal(Enumerable.Range(0, removed.Length), rows.Where(r => r.Removed >= 0).Select(r => r.Removed));
            Assert.Equal(Enumerable.Range(0, added.Length), rows.Where(r => r.Added >= 0).Select(r => r.Added));
            Assert.DoesNotContain(rows, r => r.Removed < 0 && r.Added < 0);
        }
    }

    [Fact]
    public void Huge_blocks_skip_similarity_and_zip()
    {
        var removed = Enumerable.Range(0, 200).Select(i => $"old {i}").ToArray();
        var added = Enumerable.Range(0, 100).Select(i => $"new {i}").ToArray();
        var rows = LinePairing.Pair(removed, added);
        Assert.Equal(200, rows.Count);
        Assert.Equal((0, 0), rows[0]);
        Assert.Equal((150, -1), rows[150]);
    }

    [Fact]
    public void Side_by_side_places_similar_lines_on_the_same_row()
    {
        var diff = DiffUtil.ComputeFull(
            "a\nint count = 1;\nz",
            "a\n// 件数を数える\nint count = 2;\nz");
        var rows = SideBySideDiff.Build(diff);
        Assert.Equal(new SideBySideRow(SideCellKind.Empty, "", SideCellKind.Added, "// 件数を数える", null, 2), rows[1]);
        Assert.Equal(new SideBySideRow(SideCellKind.Removed, "int count = 1;", SideCellKind.Added, "int count = 2;", 2, 3), rows[2]);
    }
}
