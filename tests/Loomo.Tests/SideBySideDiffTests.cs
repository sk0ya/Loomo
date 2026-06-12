using System.Linq;
using sk0ya.Loomo.Core.Diff;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 統合差分→左右並び変換（<see cref="SideBySideDiff"/>）の検証。
/// Diff セッションの「左右」表示モードのデータ源になる。
/// </summary>
public class SideBySideDiffTests
{
    [Fact]
    public void Build_pairs_removed_and_added_runs_line_by_line()
    {
        var rows = SideBySideDiff.Build(DiffUtil.Compute("a\nold1\nold2\nz", "a\nnew1\nnew2\nz"));

        Assert.Equal(4, rows.Count);
        Assert.Equal(new SideBySideRow(SideCellKind.Context, "a", SideCellKind.Context, "a"), rows[0]);
        Assert.Equal(new SideBySideRow(SideCellKind.Removed, "old1", SideCellKind.Added, "new1"), rows[1]);
        Assert.Equal(new SideBySideRow(SideCellKind.Removed, "old2", SideCellKind.Added, "new2"), rows[2]);
        Assert.Equal(new SideBySideRow(SideCellKind.Context, "z", SideCellKind.Context, "z"), rows[3]);
    }

    [Fact]
    public void Build_fills_unbalanced_runs_with_empty_cells()
    {
        // 1行削除 → 3行追加：右が2行余るので左は Empty で埋まる
        var rows = SideBySideDiff.Build(DiffUtil.Compute("a\nold\nz", "a\nn1\nn2\nn3\nz"));

        var changes = rows.Where(r => r.LeftKind != SideCellKind.Context).ToList();
        Assert.Equal(3, changes.Count);
        Assert.Equal(new SideBySideRow(SideCellKind.Removed, "old", SideCellKind.Added, "n1"), changes[0]);
        Assert.Equal(new SideBySideRow(SideCellKind.Empty, "", SideCellKind.Added, "n2"), changes[1]);
        Assert.Equal(new SideBySideRow(SideCellKind.Empty, "", SideCellKind.Added, "n3"), changes[2]);
    }

    [Fact]
    public void Build_emits_gap_as_shared_row()
    {
        // 変更2か所の間に長い無変更区間 → DiffUtil が Gap を挟む
        var oldText = "x\n" + string.Join('\n', Enumerable.Range(1, 20)) + "\ny";
        var newText = "X\n" + string.Join('\n', Enumerable.Range(1, 20)) + "\nY";
        var rows = SideBySideDiff.Build(DiffUtil.Compute(oldText, newText));

        var gap = Assert.Single(rows, r => r.LeftKind == SideCellKind.Gap);
        Assert.Equal(SideCellKind.Gap, gap.RightKind);
        Assert.Equal(gap.LeftText, gap.RightText);
    }

    [Fact]
    public void FromUnifiedPatch_parses_headers_hunks_and_pairs_changes()
    {
        var patch = string.Join('\n',
            "diff --git a/f.txt b/f.txt",
            "index 111..222 100644",
            "--- a/f.txt",
            "+++ b/f.txt",
            "@@ -1,3 +1,3 @@",
            " ctx",
            "-old",
            "+new",
            "+extra",
            "\\ No newline at end of file");
        var rows = SideBySideDiff.FromUnifiedPatch(patch);

        // ヘッダ4行は左右共通
        Assert.All(rows.Take(4), r => Assert.Equal(SideCellKind.Header, r.LeftKind));
        Assert.Equal(SideCellKind.Gap, rows[4].LeftKind);
        // 本文：接頭辞（+/-/空白）は剥がされる
        Assert.Equal(new SideBySideRow(SideCellKind.Context, "ctx", SideCellKind.Context, "ctx"), rows[5]);
        Assert.Equal(new SideBySideRow(SideCellKind.Removed, "old", SideCellKind.Added, "new"), rows[6]);
        Assert.Equal(new SideBySideRow(SideCellKind.Empty, "", SideCellKind.Added, "extra"), rows[7]);
        Assert.Equal(SideCellKind.Header, rows[8].LeftKind); // "\ No newline…"
    }

    [Fact]
    public void FromUnifiedPatch_pairs_changes_before_next_header()
    {
        // 削除だけで終わるファイルの直後に次のファイルヘッダが来ても、対化が閉じる
        var patch = string.Join('\n',
            "@@ -1 +0,0 @@",
            "-gone",
            "diff --git a/g.txt b/g.txt");
        var rows = SideBySideDiff.FromUnifiedPatch(patch);

        Assert.Equal(new SideBySideRow(SideCellKind.Removed, "gone", SideCellKind.Empty, ""), rows[1]);
        Assert.Equal(SideCellKind.Header, rows[2].LeftKind);
    }
}
