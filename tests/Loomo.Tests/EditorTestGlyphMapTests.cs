using System;
using System.IO;
using System.Linq;
using Editor.Controls.Rendering;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>エディタのガターに出す「テスト実行 ▶ ／結果」グリフの組み立て（純ロジック）の検証。
/// 行の基準（テスト側 1 始まり → バッファ 0 始まり）・結果種別の対応・ツールチップ・
/// テストが無いファイルで列を出さないこと・マルチルートのパス一致を見る。</summary>
public class EditorTestGlyphMapTests
{
    private static readonly string RootA = Path.Combine(Path.GetTempPath(), "loomo-glyph", "app");
    private static readonly string RootB = Path.Combine(Path.GetTempPath(), "loomo-glyph", "lib");

    private static FakeWorkspaceService Workspace(params string[] folders)
    {
        var ws = new FakeWorkspaceService();
        if (folders.Length > 0) ws.OpenFolder(folders[0]);
        foreach (var f in folders.Skip(1)) ws.AddFolder(f);
        return ws;
    }

    private static TestItemViewModel Item(string fqn, string? path, int line1, TestStatus status = TestStatus.NotRun,
        string? message = null, TimeSpan? duration = null)
    {
        var t = new TestItemViewModel(fqn) { DeclarationPath = path, DeclarationLine = line1 };
        if (status != TestStatus.NotRun) t.Update(status, message, null, 0, duration);
        return t;
    }

    [Fact]
    public void Converts_1_based_declaration_line_to_0_based_buffer_line()
    {
        var file = Path.Combine(RootA, "WidgetTests.cs");
        var glyphs = EditorTestGlyphMap.Build(Workspace(RootA), [Item("N.C.M", file, 12)], file);

        var g = Assert.Single(glyphs);
        Assert.Equal(11, g.Line0);
        Assert.Equal(TestGlyphKind.Run, g.Kind);
    }

    [Theory]
    [InlineData(TestStatus.NotRun, TestGlyphKind.Run)]
    [InlineData(TestStatus.Running, TestGlyphKind.Running)]
    [InlineData(TestStatus.Passed, TestGlyphKind.Passed)]
    [InlineData(TestStatus.Failed, TestGlyphKind.Failed)]
    [InlineData(TestStatus.Skipped, TestGlyphKind.Skipped)]
    public void Maps_each_status_to_its_glyph(TestStatus status, TestGlyphKind expected)
    {
        var file = Path.Combine(RootA, "T.cs");
        var item = Item("N.C.M", file, 3);
        if (status == TestStatus.Running) item.SetRunning();
        else if (status != TestStatus.NotRun) item.Update(status, null, null, 0);

        var g = Assert.Single(EditorTestGlyphMap.Build(Workspace(RootA), [item], file));
        Assert.Equal(expected, g.Kind);
    }

    [Fact]
    public void Tooltip_shows_failure_message_and_duration()
    {
        var file = Path.Combine(RootA, "T.cs");
        var item = Item("N.C.Broken", file, 5, TestStatus.Failed, "Assert.Equal() Failure",
            TimeSpan.FromMilliseconds(42));

        var g = Assert.Single(EditorTestGlyphMap.Build(Workspace(RootA), [item], file));
        Assert.Contains("✗ 失敗", g.Tooltip);
        Assert.Contains("42 ms", g.Tooltip);
        Assert.Contains("Assert.Equal() Failure", g.Tooltip);
        Assert.Contains("クリックで再実行", g.Tooltip);
    }

    [Fact]
    public void Tooltip_of_unrun_test_offers_to_run_it()
    {
        var file = Path.Combine(RootA, "T.cs");
        var g = Assert.Single(EditorTestGlyphMap.Build(Workspace(RootA), [Item("N.C.M", file, 5)], file));
        Assert.StartsWith("▶ テストを実行", g.Tooltip);
        Assert.DoesNotContain("ms", g.Tooltip);   // 未実行に所要時間は出さない
    }

    [Fact]
    public void Long_duration_is_shown_in_seconds()
    {
        var file = Path.Combine(RootA, "T.cs");
        var item = Item("N.C.Slow", file, 5, TestStatus.Passed, null, TimeSpan.FromMilliseconds(1500));
        var g = Assert.Single(EditorTestGlyphMap.Build(Workspace(RootA), [item], file));
        Assert.Contains("1.50 秒", g.Tooltip);
    }

    [Theory]
    // 丸めてから単位を選ぶ。先に閾値で分けると 999.6ms が「1000 ms」と出る。
    [InlineData(0, "（0 ms）")]
    [InlineData(999.4, "（999 ms）")]
    [InlineData(999.6, "（1.00 秒）")]
    [InlineData(1000, "（1.00 秒）")]
    [InlineData(1500, "（1.50 秒）")]
    public void Duration_is_rounded_before_the_unit_is_chosen(double milliseconds, string expected)
        => Assert.Equal(expected, EditorTestGlyphMap.FormatDuration(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void Unknown_or_negative_duration_prints_nothing()
    {
        Assert.Equal("", EditorTestGlyphMap.FormatDuration(null));
        Assert.Equal("", EditorTestGlyphMap.FormatDuration(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void File_without_tests_yields_no_glyphs_so_the_column_stays_hidden()
    {
        var tests = new[] { Item("N.C.M", Path.Combine(RootA, "OtherTests.cs"), 4) };
        Assert.Empty(EditorTestGlyphMap.Build(Workspace(RootA), tests, Path.Combine(RootA, "Widget.cs")));
        Assert.Empty(EditorTestGlyphMap.Build(Workspace(RootA), tests, null));
        Assert.Empty(EditorTestGlyphMap.Build(Workspace(RootA), [], Path.Combine(RootA, "OtherTests.cs")));
    }

    [Fact]
    public void Tests_without_a_known_declaration_line_are_dropped()
    {
        var file = Path.Combine(RootA, "T.cs");
        // dotnet 側は走査でファイルが判らない場合 SourcePath=null / Line1=0 で来る。
        Assert.Empty(EditorTestGlyphMap.Build(Workspace(RootA), [Item("N.C.M", file, 0)], file));
        Assert.Empty(EditorTestGlyphMap.Build(Workspace(RootA), [Item("N.C.M", null, 3)], file));
    }

    [Fact]
    public void Running_then_finished_moves_the_same_line_from_running_to_result()
    {
        var file = Path.Combine(RootA, "T.cs");
        var item = Item("N.C.M", file, 7);
        var ws = Workspace(RootA);

        Assert.Equal(TestGlyphKind.Run, Assert.Single(EditorTestGlyphMap.Build(ws, [item], file)).Kind);

        item.SetRunning();
        var running = Assert.Single(EditorTestGlyphMap.Build(ws, [item], file));
        Assert.Equal(TestGlyphKind.Running, running.Kind);
        Assert.Equal(6, running.Line0);
        Assert.Contains("実行中", running.Tooltip);

        item.Update(TestStatus.Passed, null, null, 0, TimeSpan.FromMilliseconds(8));
        var done = Assert.Single(EditorTestGlyphMap.Build(ws, [item], file));
        Assert.Equal(TestGlyphKind.Passed, done.Kind);
        Assert.Equal(6, done.Line0);
        Assert.Contains("8 ms", done.Tooltip);
    }

    [Fact]
    public void Several_tests_on_one_line_collapse_to_the_worst_status()
    {
        var file = Path.Combine(RootA, "suite.test.ts");
        var passed = Item("a", file, 9, TestStatus.Passed);
        var failed = Item("b", file, 9, TestStatus.Failed, "boom");

        var g = Assert.Single(EditorTestGlyphMap.Build(Workspace(RootA), [passed, failed], file));
        Assert.Equal(TestGlyphKind.Failed, g.Kind);
        Assert.Contains("✓ 成功", g.Tooltip);
        Assert.Contains("✗ 失敗", g.Tooltip);
    }

    [Fact]
    public void Added_folders_get_glyphs_too_not_just_the_primary()
    {
        var ws = Workspace(RootA, RootB);
        var inAdded = Path.Combine(RootB, "LibTests.cs");

        var g = Assert.Single(EditorTestGlyphMap.Build(ws, [Item("N.C.M", inAdded, 2)], inAdded));
        Assert.Equal(1, g.Line0);
    }

    [Fact]
    public void Files_outside_the_workspace_get_no_glyphs()
    {
        var outside = Path.Combine(Path.GetTempPath(), "loomo-glyph", "elsewhere", "T.cs");
        Assert.Empty(EditorTestGlyphMap.Build(Workspace(RootA), [Item("N.C.M", outside, 2)], outside));
    }

    [Fact]
    public void Sibling_folder_with_a_shared_prefix_is_not_treated_as_inside()
    {
        // "…\app2" は "…\app" 配下ではない（区切り無しの前方一致の罠）。
        var sibling = Path.Combine(Path.GetTempPath(), "loomo-glyph", "app2", "T.cs");
        Assert.Empty(EditorTestGlyphMap.Build(Workspace(RootA), [Item("N.C.M", sibling, 2)], sibling));
    }

    [Fact]
    public void Path_comparison_ignores_case_and_relative_segments()
    {
        var file = Path.Combine(RootA, "T.cs");
        var spelled = Path.Combine(RootA, "sub", "..", "T.cs").ToUpperInvariant();

        Assert.Single(EditorTestGlyphMap.Build(Workspace(RootA), [Item("N.C.M", spelled, 4)], file));
    }

    [Fact]
    public void Tests_at_returns_the_items_declared_on_that_buffer_line()
    {
        var file = Path.Combine(RootA, "T.cs");
        var first = Item("N.C.A", file, 10);
        var second = Item("N.C.B", file, 20);
        var ws = Workspace(RootA);

        Assert.Same(first, Assert.Single(EditorTestGlyphMap.TestsAt(ws, [first, second], file, 9)));
        Assert.Same(second, Assert.Single(EditorTestGlyphMap.TestsAt(ws, [first, second], file, 19)));
        Assert.Empty(EditorTestGlyphMap.TestsAt(ws, [first, second], file, 14));
    }

    [Fact]
    public void Caret_picks_the_declaration_on_or_above_the_caret()
    {
        var file = Path.Combine(RootA, "T.cs");
        var first = Item("N.C.A", file, 10);
        var second = Item("N.C.B", file, 20);
        var ws = Workspace(RootA);
        TestItemViewModel[] tests = [first, second];

        Assert.Same(first, EditorTestGlyphMap.TestForCaret(ws, tests, file, 9));    // 宣言行そのもの
        Assert.Same(first, EditorTestGlyphMap.TestForCaret(ws, tests, file, 14));   // 本文の中
        Assert.Same(second, EditorTestGlyphMap.TestForCaret(ws, tests, file, 30));  // 最後のテストの本文
        Assert.Null(EditorTestGlyphMap.TestForCaret(ws, tests, file, 3));           // 最初のテストより上
        Assert.Null(EditorTestGlyphMap.TestForCaret(ws, tests, Path.Combine(RootA, "Other.cs"), 30));
    }

    [Fact]
    public void Caret_far_below_the_last_test_finds_nothing()
    {
        var file = Path.Combine(RootA, "T.cs");
        var test = Item("N.C.A", file, 10);
        var ws = Workspace(RootA);
        TestItemViewModel[] tests = [test];

        var lastInside = 10 + EditorTestGlyphMap.MaxCaretLookback - 1;   // 0 始まりのキャレット行
        Assert.Same(test, EditorTestGlyphMap.TestForCaret(ws, tests, file, lastInside));
        Assert.Null(EditorTestGlyphMap.TestForCaret(ws, tests, file, lastInside + 1));
    }
}

/// <summary>ガターのテスト列を出すかの記憶（本文左端のちらつき防止）の検証。</summary>
public class EditorTestGlyphColumnsTests
{
    private static readonly string File1 = Path.Combine(Path.GetTempPath(), "loomo-glyph", "app", "T.cs");
    private static readonly string File2 = Path.Combine(Path.GetTempPath(), "loomo-glyph", "app", "U.cs");

    [Fact]
    public void Column_stays_open_once_the_file_is_known_to_hold_tests()
    {
        var columns = new EditorTestGlyphColumns();
        Assert.False(columns.ShouldEnable(File1, 0));   // まだテストソースと判っていない
        Assert.True(columns.ShouldEnable(File1, 2));    // 見つかった
        // 編集途中でパーサが拾えなくなっても畳まない（畳むと本文が左右に動く）。
        Assert.True(columns.ShouldEnable(File1, 0));
    }

    [Fact]
    public void Other_files_are_unaffected()
    {
        var columns = new EditorTestGlyphColumns();
        columns.ShouldEnable(File1, 1);
        Assert.False(columns.ShouldEnable(File2, 0));
    }

    [Fact]
    public void The_same_file_spelled_differently_is_the_same_file()
    {
        var columns = new EditorTestGlyphColumns();
        columns.ShouldEnable(File1, 1);
        var spelled = Path.Combine(Path.GetTempPath(), "loomo-glyph", "app", "sub", "..", "T.cs").ToUpperInvariant();
        Assert.True(columns.ShouldEnable(spelled, 0));
    }

    [Fact]
    public void Reset_forgets_everything_and_an_empty_path_never_enables()
    {
        var columns = new EditorTestGlyphColumns();
        columns.ShouldEnable(File1, 1);
        columns.Reset();
        Assert.False(columns.ShouldEnable(File1, 0));
        Assert.False(columns.ShouldEnable(null, 3));
        Assert.False(columns.ShouldEnable("  ", 3));
    }
}
