using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Core.Diff;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 行差分（<see cref="DiffUtil"/>）の検証：差分として正しいこと、変更のかたまりを読みやすい位置へ寄せること
/// （<see cref="DiffCompaction"/>）、空白を無視した比較。
/// </summary>
public class DiffUtilTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    /// <summary>差分から旧・新の全文を組み立て直す（差分が両側を正しく表しているか）。</summary>
    private static (string Old, string New) Reconstruct(IReadOnlyList<DiffLine> diff)
    {
        var oldLines = diff.Where(l => l.Kind != DiffLineKind.Added).Select(l => l.LeftText);
        var newLines = diff.Where(l => l.Kind != DiffLineKind.Removed).Select(l => l.Text);
        return (string.Join("\n", oldLines), string.Join("\n", newLines));
    }

    private static string Render(IReadOnlyList<DiffLine> diff)
        => string.Join("|", diff.Select(l => l.Kind switch
        {
            DiffLineKind.Added => "+" + l.Text,
            DiffLineKind.Removed => "-" + l.Text,
            _ => " " + l.Text,
        }));

    [Fact]
    public void Random_texts_round_trip_through_the_diff()
    {
        var rng = new Random(2024);
        var vocabulary = new[] { "", "{", "}", "a();", "b();", "  x", "return;", "// c" };
        for (var trial = 0; trial < 2000; trial++)
        {
            string RandomText() => string.Join("\n",
                Enumerable.Range(0, rng.Next(25)).Select(_ => vocabulary[rng.Next(vocabulary.Length)]));
            var oldText = RandomText();
            var newText = RandomText();
            var diff = DiffUtil.ComputeFull(oldText, newText);
            var (o, n) = Reconstruct(diff);
            Assert.Equal(oldText, o);
            Assert.Equal(newText, n);
        }
    }

    [Fact]
    public void Diff_is_minimal_after_compaction()
    {
        // 寄せは同じ長さの候補の間でしか動かさない（差分が長くならない）。
        var rng = new Random(77);
        var vocabulary = new[] { "", "{", "}", "a", "b" };
        for (var trial = 0; trial < 1000; trial++)
        {
            var a = Enumerable.Range(0, rng.Next(20)).Select(_ => vocabulary[rng.Next(vocabulary.Length)]).ToArray();
            var b = Enumerable.Range(0, rng.Next(20)).Select(_ => vocabulary[rng.Next(vocabulary.Length)]).ToArray();
            var diff = DiffUtil.ComputeFull(string.Join("\n", a), string.Join("\n", b));
            // 空文字列は「0行」として読まれる（空行1つだけの列と区別できない）ので、その読み方に揃える。
            if (a is [""]) a = [];
            if (b is [""]) b = [];
            var expectedChanges = a.Length + b.Length - 2 * Lcs(a, b);
            Assert.Equal(expectedChanges, diff.Count(l => l.Kind != DiffLineKind.Context));
        }
    }

    private static int Lcs(string[] a, string[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        return dp[0, 0];
    }

    [Fact]
    public void Change_runs_list_removals_before_additions()
    {
        var diff = DiffUtil.ComputeFull(Lines("a", "x1", "x2", "b"), Lines("a", "y1", "b"));
        Assert.Equal(" a|-x1|-x2|+y1| b", Render(diff));
    }

    [Fact]
    public void Inserted_function_is_placed_between_blank_lines()
    {
        // 素の最短経路だと「A の } と空行と B() {」が足されたように見える位置を選びがち。
        var oldText = Lines("A() {", "}", "", "C() {", "}");
        var newText = Lines("A() {", "}", "", "B() {", "}", "", "C() {", "}");
        var diff = DiffUtil.ComputeFull(oldText, newText);
        Assert.Equal(" A() {| }| |+B() {|+}|+| C() {| }", Render(diff));
    }

    [Fact]
    public void Removed_function_is_placed_between_blank_lines()
    {
        var oldText = Lines("A() {", "}", "", "B() {", "}", "", "C() {", "}");
        var newText = Lines("A() {", "}", "", "C() {", "}");
        var diff = DiffUtil.ComputeFull(oldText, newText);
        Assert.Equal(" A() {| }| |-B() {|-}|-| C() {| }", Render(diff));
    }

    [Fact]
    public void Appended_block_without_blank_lines_slides_to_the_end()
    {
        // 空行の手がかりが無いときは一番下（git の既定と同じ）。
        var oldText = Lines("if (x)", "    y();");
        var newText = Lines("if (x)", "    y();", "if (z)", "    y();");
        var diff = DiffUtil.ComputeFull(oldText, newText);
        Assert.Equal(" if (x)|     y();|+if (z)|+    y();", Render(diff));
    }

    [Fact]
    public void Compaction_does_not_merge_neighbouring_change_groups()
    {
        // 寄せた先で別の変更とくっつくと、左右の組み合わせが変わってしまう。間の一致行は残す。
        // 旧 [x, a] → 新 [X, a, a]：足した「a」は1つ上へ寄せられるが、寄せると x→X の書き換えとくっつく。
        var edits = new List<Edit>
        {
            new(EditKind.Delete, 0, -1), new(EditKind.Insert, -1, 0),
            new(EditKind.Equal, 1, 1), new(EditKind.Insert, -1, 2),
        };
        var expected = edits.ToList();
        DiffCompaction.Compact(edits, aKeys: [0, 1], bKeys: [2, 1, 1],
            aBlank: [false, false], bBlank: [false, false, false]);
        Assert.Equal(expected, edits);
    }

    [Fact]
    public void Compaction_slides_within_the_room_left_between_groups()
    {
        // 旧 [x, a, b, a] → 新 [X, a, b, a, b, a]：足した「b, a」は上へ2つまで寄せられるが、
        // 空行の手がかりが無いので一番下のまま。どの位置でも書き換え（x→X）との間に一致行が残る。
        var edits = new List<Edit>
        {
            new(EditKind.Delete, 0, -1), new(EditKind.Insert, -1, 0),
            new(EditKind.Equal, 1, 1), new(EditKind.Equal, 2, 2), new(EditKind.Equal, 3, 3),
            new(EditKind.Insert, -1, 4), new(EditKind.Insert, -1, 5),
        };
        var expected = edits.ToList();
        DiffCompaction.Compact(edits, aKeys: [0, 1, 2, 1], bKeys: [3, 1, 2, 1, 2, 1],
            aBlank: new bool[4], bBlank: new bool[6]);
        Assert.Equal(expected, edits);
    }

    [Fact]
    public void Compaction_moves_a_group_up_to_a_blank_line_boundary()
    {
        // 新 [A, "", B, "", C]（旧 [A, "", C]）を、最短経路が「"", B」を足したように置いた場合。
        // 「B, ""」（空行の直後に始まり空行で終わる）へ寄せる。
        var edits = new List<Edit>
        {
            new(EditKind.Equal, 0, 0),
            new(EditKind.Insert, -1, 1), new(EditKind.Insert, -1, 2),
            new(EditKind.Equal, 1, 3), new(EditKind.Equal, 2, 4),
        };
        DiffCompaction.Compact(edits, aKeys: [0, 1, 2], bKeys: [0, 1, 3, 1, 2],
            aBlank: [false, true, false], bBlank: [false, true, false, true, false]);
        Assert.Equal(new List<Edit>
        {
            new(EditKind.Equal, 0, 0), new(EditKind.Equal, 1, 1),
            new(EditKind.Insert, -1, 2), new(EditKind.Insert, -1, 3),
            new(EditKind.Equal, 2, 4),
        }, edits);
    }

    [Fact]
    public void Rewrite_groups_are_not_moved()
    {
        // 削除と追加が混ざったかたまり（書き換え）は動かさない。
        var diff = DiffUtil.ComputeFull(Lines("a", "", "b", ""), Lines("a", "", "c", ""));
        Assert.Equal(" a| |-b|+c| ", Render(diff));
    }

    [Fact]
    public void Ignore_whitespace_treats_reindented_lines_as_context()
    {
        var oldText = Lines("if (x) {", "foo();", "}");
        var newText = Lines("if (x) {", "    foo();", "}");

        Assert.Equal(2, DiffUtil.ComputeFull(oldText, newText).Count(l => l.Kind != DiffLineKind.Context));

        var ignored = DiffUtil.ComputeFull(oldText, newText, new DiffOptions(IgnoreWhitespace: true));
        Assert.All(ignored, l => Assert.Equal(DiffLineKind.Context, l.Kind));
        // 文脈行でも左右それぞれの綴りを保つ（左は旧側・右は新側）。
        Assert.Equal("foo();", ignored[1].LeftText);
        Assert.Equal("    foo();", ignored[1].Text);
        Assert.Null(ignored[0].OldText);   // 綴りが同じ行には旧側の綴りを持たせない
    }

    [Fact]
    public void Ignore_whitespace_still_reports_real_changes()
    {
        var options = new DiffOptions(IgnoreWhitespace: true);
        var diff = DiffUtil.ComputeFull(Lines("a = 1;", "b = 2;"), Lines("a=1;", "b = 3;"), options);
        Assert.Equal(" a=1;|-b = 2;|+b = 3;", Render(diff));
        Assert.Equal((1, 1), DiffUtil.Stat("a = 1;\nb = 2;", "a=1;\nb = 3;", options));
    }

    [Fact]
    public void Ignore_whitespace_flows_into_side_by_side_with_each_sides_spelling()
    {
        var diff = DiffUtil.ComputeFull("x\ty", "x  y", new DiffOptions(IgnoreWhitespace: true));
        var row = Assert.Single(SideBySideDiff.Build(diff));
        Assert.Equal(new SideBySideRow(SideCellKind.Context, "x\ty", SideCellKind.Context, "x  y", 1, 1), row);
    }

    [Fact]
    public void Large_files_are_diffed_instead_of_replaced_wholesale()
    {
        // 旧実装は 5000×5000 行を超えると「全部削除して全部追加」に落ちていた。
        var oldLines = Enumerable.Range(0, 8000).Select(i => $"line {i}").ToArray();
        var newLines = oldLines.ToList();
        newLines[4000] = "changed";
        var diff = DiffUtil.ComputeFull(string.Join("\n", oldLines), string.Join("\n", newLines));
        Assert.Equal(2, diff.Count(l => l.Kind != DiffLineKind.Context));
        Assert.Equal((1, 1), DiffUtil.Stat(string.Join("\n", oldLines), string.Join("\n", newLines)));
    }

    [Fact]
    public void Hunks_are_unchanged_by_the_new_algorithm()
    {
        // 承認カードの差分（ハンク＋省略）の形は従来どおり。
        var oldText = "x\n" + string.Join('\n', Enumerable.Range(1, 20)) + "\ny";
        var newText = "X\n" + string.Join('\n', Enumerable.Range(1, 20)) + "\nY";
        var diff = DiffUtil.Compute(oldText, newText);
        Assert.Equal(DiffLineKind.Removed, diff[0].Kind);
        Assert.Equal(DiffLineKind.Added, diff[1].Kind);
        Assert.Single(diff, l => l.Kind == DiffLineKind.Gap);
        Assert.Equal(" … 14 行省略 …", diff.Single(l => l.Kind == DiffLineKind.Gap).Text);
    }
}
