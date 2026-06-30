using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.Core.Diff;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 行単位の差分破棄（<see cref="UnifiedPatchEditor"/>）の検証。Diff セッションで選んだ行だけを
/// <c>git apply --reverse --recount</c> へ渡す縮約パッチを組み立てる部分。
/// </summary>
public class UnifiedPatchEditorTests
{
    // 1ファイル分の典型的な unified diff（context=3）。行インデックスはコメントの通り。
    private const string Patch =
        "diff --git a/f.txt b/f.txt\n" +   // 0
        "index 1111111..2222222 100644\n" + // 1
        "--- a/f.txt\n" +                   // 2
        "+++ b/f.txt\n" +                   // 3
        "@@ -1,5 +1,6 @@\n" +               // 4
        " ctx1\n" +                         // 5
        "-old2\n" +                         // 6
        "+new2\n" +                         // 7
        "+added3\n" +                       // 8
        " ctx4\n" +                         // 9
        " ctx5\n";                          // 10

    private static IReadOnlySet<int> Sel(params int[] idx) => idx.ToHashSet();

    [Fact]
    public void Discarding_an_added_line_keeps_it_as_addition_and_demotes_other_additions()
    {
        // index 8（+added3）だけ破棄：残す +new2 は文脈行へ、-old2 は落ちる
        var result = UnifiedPatchEditor.BuildReverseDiscardPatch(Patch, Sel(8));

        Assert.False(result.IsEmpty);
        Assert.Equal(1, result.DiscardedLineCount);
        var lines = result.Patch.TrimEnd('\n').Split('\n');
        Assert.Equal(new[]
        {
            "diff --git a/f.txt b/f.txt",
            "index 1111111..2222222 100644",
            "--- a/f.txt",
            "+++ b/f.txt",
            "@@ -1,5 +1,6 @@",
            " ctx1",
            " new2",     // 残す追加 → 文脈行へ降格
            "+added3",   // 破棄対象の追加はそのまま
            " ctx4",
            " ctx5",
        }, lines);
        // 残す削除（-old2）は出力に含めない
        Assert.DoesNotContain("-old2", lines);
    }

    [Fact]
    public void Discarding_a_removed_line_keeps_it_so_reverse_apply_restores_it()
    {
        // index 6（-old2）だけ破棄：削除行のまま残り、他の追加は文脈行へ降格
        var result = UnifiedPatchEditor.BuildReverseDiscardPatch(Patch, Sel(6));

        Assert.Equal(1, result.DiscardedLineCount);
        var lines = result.Patch.TrimEnd('\n').Split('\n');
        Assert.Contains("-old2", lines);
        Assert.Contains(" new2", lines);   // 残す追加 → 文脈
        Assert.Contains(" added3", lines); // 残す追加 → 文脈
        Assert.DoesNotContain("+new2", lines);
        Assert.DoesNotContain("+added3", lines);
    }

    [Fact]
    public void Selecting_context_or_header_lines_only_discards_nothing()
    {
        // 文脈行・ヘッダ行だけ選んでも破棄対象は無い
        var result = UnifiedPatchEditor.BuildReverseDiscardPatch(Patch, Sel(2, 4, 5, 9));

        Assert.True(result.IsEmpty);
        Assert.Equal("", result.Patch);
    }

    [Fact]
    public void Selecting_the_whole_change_keeps_all_changes()
    {
        var result = UnifiedPatchEditor.BuildReverseDiscardPatch(Patch, Sel(6, 7, 8));

        Assert.Equal(3, result.DiscardedLineCount);
        var lines = result.Patch.TrimEnd('\n').Split('\n');
        Assert.Contains("-old2", lines);
        Assert.Contains("+new2", lines);
        Assert.Contains("+added3", lines);
    }

    [Fact]
    public void Hunks_without_any_selected_change_are_dropped()
    {
        var twoHunks =
            "diff --git a/f.txt b/f.txt\n" +
            "--- a/f.txt\n" +
            "+++ b/f.txt\n" +
            "@@ -1,2 +1,2 @@\n" +   // 3
            " a\n" +                // 4
            "-b\n" +                // 5
            "+B\n" +                // 6
            "@@ -10,2 +10,2 @@\n" + // 7
            " y\n" +                // 8
            "-z\n" +                // 9
            "+Z\n";                 // 10

        // 2つ目のハンクの変更だけ選ぶ → 1つ目のハンクは出力されない
        var result = UnifiedPatchEditor.BuildReverseDiscardPatch(twoHunks, Sel(9, 10));

        Assert.Equal(2, result.DiscardedLineCount);
        var lines = result.Patch.TrimEnd('\n').Split('\n');
        Assert.Single(lines.Where(l => l.StartsWith("@@")));
        Assert.Contains("@@ -10,2 +10,2 @@", lines);
        Assert.DoesNotContain("-b", lines);
        Assert.DoesNotContain("+B", lines);
    }

    [Fact]
    public void Patch_without_hunks_yields_empty()
    {
        var result = UnifiedPatchEditor.BuildReverseDiscardPatch("# 未追跡ファイル: f.txt", Sel(0));
        Assert.True(result.IsEmpty);
    }

    // ===== 行番号（旧/新）での選択（左右並び表示の範囲破棄）=====

    [Fact]
    public void ByLineNumbers_restores_removed_and_removes_added_for_the_block()
    {
        // Patch: -old2 は旧2行目、+new2 は新2行目、+added3 は新3行目
        var result = UnifiedPatchEditor.BuildReverseDiscardPatchForLines(
            Patch, oldLinesToRestore: Sel(2), newLinesToRemove: Sel(2, 3));

        Assert.Equal(3, result.DiscardedLineCount);
        var lines = result.Patch.TrimEnd('\n').Split('\n');
        Assert.Contains("-old2", lines);
        Assert.Contains("+new2", lines);
        Assert.Contains("+added3", lines);
    }

    [Fact]
    public void ByLineNumbers_keeps_unselected_changes_as_context_or_dropped()
    {
        // 新3行目（+added3）だけ破棄。-old2 は落ち、+new2 は文脈行へ降格。
        var result = UnifiedPatchEditor.BuildReverseDiscardPatchForLines(
            Patch, oldLinesToRestore: Sel(), newLinesToRemove: Sel(3));

        Assert.Equal(1, result.DiscardedLineCount);
        var lines = result.Patch.TrimEnd('\n').Split('\n');
        Assert.Contains("+added3", lines);
        Assert.Contains(" new2", lines);     // 残す追加 → 文脈
        Assert.DoesNotContain("-old2", lines); // 残す削除 → 除去
        Assert.DoesNotContain("+new2", lines);
    }

    [Fact]
    public void ByLineNumbers_with_no_match_yields_empty()
    {
        var result = UnifiedPatchEditor.BuildReverseDiscardPatchForLines(
            Patch, oldLinesToRestore: Sel(99), newLinesToRemove: Sel(99));
        Assert.True(result.IsEmpty);
    }
}
