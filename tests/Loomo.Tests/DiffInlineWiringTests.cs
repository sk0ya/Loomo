using System.Linq;
using Editor.Core.Editing;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 行内差分の配線：統合表示の行への配り方（<see cref="DiffInlineHighlighter.ForUnified"/>・パッチの1文字を
/// 跨いだ桁合わせ）、構文色の区間との合成（<see cref="DiffSyntaxRunMapper.SplitByChanges"/>）、左右並びの
/// エディタの装飾（<see cref="DiffEditorAlignment.Build(System.Collections.Generic.IReadOnlyList{DiffSideRowVm}, int, int, System.Func{DiffSideRowVm, InlineChange?}?)"/>）と覚え書き（<see cref="DiffInlineCache"/>）。
/// </summary>
public class DiffInlineWiringTests
{
    private static string Marked(string text, TextSpan span) => text.Substring(span.Start, span.Length);

    // ===== 統合表示 =====

    [Fact]
    public void Unified_patch_rows_get_spans_shifted_past_the_prefix()
    {
        var rows = new[]
        {
            new DiffRowVm("Context", " a"),
            new DiffRowVm("Removed", "-int count = 1;"),
            new DiffRowVm("Added", "+int count = 2;"),
            new DiffRowVm("Context", " z"),
        };

        var inline = DiffInlineHighlighter.ForUnified(rows, hasPatchPrefix: true);

        Assert.Equal(4, inline.Count);
        Assert.Null(inline[0]);
        Assert.Equal("1", Marked(rows[1].Text, Assert.Single(inline[1]!)));
        Assert.Equal("2", Marked(rows[2].Text, Assert.Single(inline[2]!)));
        Assert.Null(inline[3]);
    }

    [Fact]
    public void Unified_comparison_rows_are_not_shifted()
    {
        var rows = new[] { new DiffRowVm("Removed", "foo(a)"), new DiffRowVm("Added", "foo(b)") };

        var inline = DiffInlineHighlighter.ForUnified(rows, hasPatchPrefix: false);

        Assert.Equal("a", Marked(rows[0].Text, Assert.Single(inline[0]!)));
        Assert.Equal("b", Marked(rows[1].Text, Assert.Single(inline[1]!)));
    }

    [Fact]
    public void Unified_pairs_similar_lines_across_an_inserted_line()
    {
        var rows = new[]
        {
            new DiffRowVm("Removed", "-int count = 1;"),
            new DiffRowVm("Added", "+// 件数"),
            new DiffRowVm("Added", "+int count = 2;"),
        };

        var inline = DiffInlineHighlighter.ForUnified(rows, hasPatchPrefix: true);

        Assert.Equal("1", Marked(rows[0].Text, Assert.Single(inline[0]!)));
        Assert.Null(inline[1]);                                   // 足しただけの行には付かない
        Assert.Equal("2", Marked(rows[2].Text, Assert.Single(inline[2]!)));
    }

    [Fact]
    public void Unified_pure_insertions_and_unrelated_rewrites_have_no_spans()
    {
        var rows = new[]
        {
            new DiffRowVm("Added", "+brand new"),
            new DiffRowVm("Context", " x"),
            new DiffRowVm("Removed", "-return value;"),
            new DiffRowVm("Added", "+foreach (var item in list)"),
            new DiffRowVm("Header", "\\ No newline at end of file"),
        };

        Assert.Same(DiffInlineHighlighter.None, DiffInlineHighlighter.ForUnified(rows, hasPatchPrefix: true));
    }

    [Fact]
    public void Unified_gives_up_beyond_the_line_limit()
    {
        var rows = Enumerable.Range(0, DiffInlineHighlighter.MaxLines + 1)
            .Select(i => new DiffRowVm(i % 2 == 0 ? "Removed" : "Added", $"line {i / 2}"))
            .ToArray();

        Assert.Same(DiffInlineHighlighter.None, DiffInlineHighlighter.ForUnified(rows, hasPatchPrefix: false));
    }

    // ===== 構文色の区間との合成 =====

    [Fact]
    public void SplitByChanges_cuts_runs_at_change_boundaries_and_keeps_colors()
    {
        var keyword = new object();
        var runs = new[] { new DiffSyntaxRun(0, 3, keyword), new DiffSyntaxRun(3, 10, null) };

        var split = DiffSyntaxRunMapper.SplitByChanges(runs, [new TextSpan(2, 3), new TextSpan(8, 1)]);

        Assert.Equal(new[]
        {
            new DiffSyntaxRun(0, 2, keyword, false),
            new DiffSyntaxRun(2, 3, keyword, true),
            new DiffSyntaxRun(3, 5, null, true),
            new DiffSyntaxRun(5, 8, null, false),
            new DiffSyntaxRun(8, 9, null, true),
            new DiffSyntaxRun(9, 10, null, false),
        }, split);
    }

    [Fact]
    public void SplitByChanges_without_changes_returns_runs_unchanged()
    {
        var runs = new[] { new DiffSyntaxRun(0, 4, null) };
        Assert.Same(runs, DiffSyntaxRunMapper.SplitByChanges(runs, null));
        Assert.Same(runs, DiffSyntaxRunMapper.SplitByChanges(runs, []));
    }

    [Fact]
    public void SplitByChanges_clamps_spans_beyond_the_line()
    {
        var runs = new[] { new DiffSyntaxRun(0, 4, null) };

        var split = DiffSyntaxRunMapper.SplitByChanges(runs, [new TextSpan(2, 50)]);

        Assert.Equal(new[] { new DiffSyntaxRun(0, 2, null, false), new DiffSyntaxRun(2, 4, null, true) }, split);
    }

    // ===== 左右並びのエディタの装飾 =====

    private static DiffSideRowVm Row(string lk, string lt, string rk, string rt, string ll, string rl)
        => new(lk, lt, rk, rt, ll, rl);

    [Fact]
    public void Side_decorations_carry_inline_changes_on_each_side()
    {
        var rows = new[]
        {
            Row("Context", "a", "Context", "a", "1", "1"),
            Row("Empty", "", "Added", "// 件数", "", "2"),
            Row("Removed", "int count = 1;", "Added", "int count = 2;", "2", "3"),
        };

        var (left, right) = DiffEditorAlignment.Build(rows);

        var leftRange = Assert.Single(left.Decorations.InlineChanges[1]);   // 左のバッファ行 1
        Assert.Equal(new DiffInlineRange(12, 1), leftRange);
        var rightRange = Assert.Single(right.Decorations.InlineChanges[2]); // 右のバッファ行 2（空き行を挟まない）
        Assert.Equal(new DiffInlineRange(12, 1), rightRange);
        Assert.False(right.Decorations.InlineChanges.ContainsKey(1));        // 足しただけの行
    }

    [Fact]
    public void Side_inline_changes_survive_trailing_spacers()
    {
        var rows = new[] { Row("Removed", "x = 1", "Added", "x = 2", "1", "1") };

        // 右だけ末尾改行で1行多い → 左の末尾に空き行が付く。その組み直しで行内差分を落とさない。
        var (left, _) = DiffEditorAlignment.Build(rows, leftEditorLineCount: 1, rightEditorLineCount: 2);

        Assert.Equal(1, left.Decorations.SpacersBefore[1]);
        Assert.Single(left.Decorations.InlineChanges[0]);
    }

    [Fact]
    public void Side_spacers_stand_for_the_other_sides_change()
    {
        var rows = new[]
        {
            Row("Removed", "gone", "Empty", "", "1", ""),
            Row("Context", "a", "Context", "a", "2", "1"),
        };

        // 右は最終行が空（末尾改行）なので、左の末尾に空き行が足される——その組み直しでも種別を保つ。
        var (left, right) = DiffEditorAlignment.Build(rows, leftEditorLineCount: 2, rightEditorLineCount: 2);

        Assert.Equal(DiffDecorationKind.Removed, right.Decorations.SpacerKind);   // 右の空き行＝左で消えた行
        Assert.Equal(DiffDecorationKind.Added, left.Decorations.SpacerKind);
        Assert.Equal(1, right.Decorations.SpacersBefore[0]);
    }

    [Fact]
    public void Side_inline_changes_skip_the_bom_the_editor_strips()
    {
        var rows = new[] { Row("Removed", "﻿int a = 1;", "Added", "int a = 2;", "1", "1") };

        var (left, right) = DiffEditorAlignment.Build(rows);

        Assert.Equal(new DiffInlineRange(8, 1), Assert.Single(left.Decorations.InlineChanges[0]));
        Assert.Equal(new DiffInlineRange(8, 1), Assert.Single(right.Decorations.InlineChanges[0]));
    }

    [Fact]
    public void Inline_cache_reuses_results_and_forgets_unused_pairs()
    {
        var cache = new DiffInlineCache();
        var first = Row("Removed", "a = 1", "Added", "a = 2", "1", "1");
        var second = Row("Removed", "b = 1", "Added", "b = 2", "2", "2");

        cache.Rotate();
        var computed = cache.Get(first);
        cache.Get(second);
        Assert.Equal(2, cache.Count);

        // 次の組み立てでは first だけ使う：同じ結果（同じインスタンス）を返し、second は覚えない。
        cache.Rotate();
        Assert.Same(computed, cache.Get(first));
        Assert.Equal(1, cache.Count);
        cache.Rotate();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Build_asks_the_inline_source_only_for_rewritten_rows()
    {
        var rows = new[]
        {
            Row("Context", "a", "Context", "a", "1", "1"),
            Row("Removed", "x", "Empty", "", "2", ""),
            Row("Removed", "y = 1", "Added", "y = 2", "3", "2"),
        };
        var asked = new List<DiffSideRowVm>();

        DiffEditorAlignment.Build(rows, 3, 2, row => { asked.Add(row); return null; });

        Assert.Equal(new[] { rows[2] }, asked);
    }

    [Fact]
    public void Recompute_with_ignore_whitespace_keeps_reindented_lines_as_context()
    {
        var rows = DiffEditorAlignment.Recompute(["if (x)", "y();"], "if (x)\n    y();\n",
            new DiffOptions(IgnoreWhitespace: true));

        Assert.All(rows, r => Assert.Equal("Context", r.LeftKind));
        Assert.Equal(("y();", "    y();"), (rows[1].LeftText, rows[1].RightText));
    }

    [Fact]
    public void Rediff_rebuilds_full_rows_with_options_and_real_line_numbers()
    {
        var rows = SideBySideDiff.FromUnifiedPatch(string.Join('\n',
            "@@ -1,3 +1,3 @@",
            " a",
            "-b",
            "+  b",
            " c"), hideChrome: true);

        var rediffed = SideBySideDiff.Rediff(rows, new DiffOptions(IgnoreWhitespace: true));

        Assert.Equal(3, rediffed.Count);
        Assert.Equal(new SideBySideRow(SideCellKind.Context, "b", SideCellKind.Context, "  b", 2, 2), rediffed[1]);
    }
}
