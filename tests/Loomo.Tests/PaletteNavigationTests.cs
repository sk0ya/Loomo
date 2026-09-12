using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Abstractions;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>コマンドパレットの「検索して飛ぶ」側（モード判定・結果の写し取り・プレビュー）の検証。</summary>
public class PaletteNavigationTests
{
    private static Action Noop(PaletteTarget _) => () => { };

    [Theory]
    [InlineData("", PaletteMode.Command, "")]
    [InlineData("エディタ", PaletteMode.Command, "エディタ")]
    [InlineData(">エディタ", PaletteMode.Command, "エディタ")]
    [InlineData("/shell", PaletteMode.File, "shell")]
    [InlineData("/ shell ", PaletteMode.File, "shell")]
    [InlineData("#TODO", PaletteMode.Text, "TODO")]
    [InlineData("@PaletteFilter", PaletteMode.Symbol, "PaletteFilter")]
    [InlineData(":120", PaletteMode.Line, "120")]
    public void Parse_reads_mode_from_first_character(string input, PaletteMode mode, string text)
    {
        var query = PaletteQuery.Parse(input);
        Assert.Equal(mode, query.Mode);
        Assert.Equal(text, query.Text);
    }

    [Fact]
    public void Command_is_the_default_and_is_not_navigation()
    {
        Assert.False(PaletteQuery.Parse("移動").IsNavigation);
        Assert.True(PaletteQuery.Parse("/移動").IsNavigation);
    }

    [Theory]
    [InlineData(":42", 42)]
    [InlineData(":0", null)]
    [InlineData(":abc", null)]
    [InlineData("42", null)]   // コマンドモードの "42" は行番号ではない
    public void LineNumber_only_parses_in_line_mode(string input, int? expected)
        => Assert.Equal(expected, PaletteQuery.Parse(input).LineNumber);

    [Fact]
    public void Tab_keeps_the_typed_text_while_switching_mode()
    {
        var query = PaletteQuery.Parse("/shell");
        var next = PaletteQuery.NextMode(query.Mode);

        Assert.Equal(PaletteMode.Text, next);
        Assert.Equal("#shell", query.ToInput(next));
        // Shift+Tab は逆回り（ファイル → コマンド）。
        Assert.Equal(PaletteMode.Command, PaletteQuery.NextMode(query.Mode, -1));
        Assert.Equal("shell", query.ToInput(PaletteMode.Command));
    }

    [Fact]
    public void NextMode_cycles_through_every_mode()
    {
        var mode = PaletteMode.Command;
        var seen = new List<PaletteMode>();
        for (var i = 0; i < 5; i++)
        {
            seen.Add(mode);
            mode = PaletteQuery.NextMode(mode);
        }

        Assert.Equal(PaletteMode.Command, mode);   // 5手で1周
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public void File_hits_show_the_name_as_title_and_the_folder_beside_it()
    {
        var items = PaletteNavigationItems.ForFiles(
            new[] { new FileSearchHit(@"C:\w\src\App\Shell.cs", "src/App/Shell.cs", 0) }, Noop);

        var item = Assert.Single(items);
        Assert.Equal("Shell.cs", item.Title);
        Assert.Equal("src/App", item.Category);
        // 行を指定しないので、開いたタブのキャレットは動かさない（0＝指定なし）。
        Assert.Equal(0, item.Target!.Line);
        Assert.Equal(@"C:\w\src\App\Shell.cs", item.Target.FullPath);
    }

    [Fact]
    public void Text_hits_carry_the_line_and_the_term_to_highlight()
    {
        var items = PaletteNavigationItems.ForText(
            new[] { new ContentSearchHit(@"C:\w\a.cs", "a.cs", 12, 5, "    var todo = 1;") },
            "todo", Noop);

        var item = Assert.Single(items);
        Assert.Equal("var todo = 1;", item.Title);
        Assert.Equal("a.cs:12", item.Category);
        Assert.Equal(12, item.Target!.Line);
        Assert.Equal("todo", item.Target.Highlight);
    }

    [Fact]
    public void Symbol_locations_keep_the_container_as_a_note()
    {
        var items = PaletteNavigationItems.ForLocations(
            new[] { new PaletteLocation(@"C:\w\a.cs", "a.cs", 30, 9, "Filter", "PaletteFilter") }, Noop);

        var item = Assert.Single(items);
        Assert.Equal("Filter", item.Title);
        Assert.Equal("a.cs:30", item.Category);
        Assert.Equal("PaletteFilter", item.Shortcut);
        Assert.Equal(30, item.Target!.Line);
    }

    [Fact]
    public void Line_item_needs_an_open_file()
    {
        Assert.Empty(PaletteNavigationItems.ForLine(null, "", 10, Noop));

        var item = Assert.Single(PaletteNavigationItems.ForLine(@"C:\w\a.cs", "a.cs", 10, Noop));
        Assert.Equal("10 行目へ", item.Title);
        Assert.Equal(10, item.Target!.Line);
    }

    [Fact]
    public void Slice_puts_the_target_line_near_the_top_and_marks_it()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"line {i}").ToList();

        var slice = PalettePreviewSlice.Slice(lines, targetLine: 50, before: 3, count: 10);

        Assert.Equal(47, slice[0].Number);
        Assert.Equal(10, slice.Count);
        Assert.Single(slice, l => l.IsTarget);
        Assert.Equal(50, slice.Single(l => l.IsTarget).Number);
    }

    [Fact]
    public void Slice_without_a_target_starts_at_the_top_and_marks_nothing()
    {
        var lines = new[] { "a", "b", "c" };

        var slice = PalettePreviewSlice.Slice(lines, targetLine: 0);

        Assert.Equal(1, slice[0].Number);
        Assert.DoesNotContain(slice, l => l.IsTarget);
    }

    [Fact]
    public void Slice_clamps_a_line_number_past_the_end_of_the_file()
    {
        var lines = new[] { "a", "b", "c" };

        var slice = PalettePreviewSlice.Slice(lines, targetLine: 999);

        Assert.Equal(3, slice.Single(l => l.IsTarget).Number);
    }

    [Fact]
    public void Slice_expands_tabs_so_the_indent_is_readable()
    {
        var slice = PalettePreviewSlice.Slice(new[] { "\tvar x = 1;" }, targetLine: 1);
        Assert.Equal("    var x = 1;", slice[0].Text);
    }

    [Fact]
    public async Task LoadAsync_reads_the_lines_around_the_target()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-palette-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 40).Select(i => $"line {i}"));
        try
        {
            var content = await PalettePreviewLoader.LoadAsync(
                new PaletteTarget(path, 20, 1, "line 20"), "a/b.txt", CancellationToken.None);

            Assert.Null(content.Message);
            Assert.Equal(Path.GetFileName(path), content.Header);
            Assert.Equal("a/b.txt:20", content.SubHeader);
            Assert.Equal("line 20", content.Highlight);
            Assert.Equal("line 20", content.Lines.Single(l => l.IsTarget).Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_says_so_instead_of_dumping_a_binary_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-palette-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02 });
        try
        {
            var content = await PalettePreviewLoader.LoadAsync(
                new PaletteTarget(path), "a.bin", CancellationToken.None);

            Assert.Empty(content.Lines);
            Assert.Contains("バイナリ", content.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_colors_the_lines_with_the_same_engine_as_the_editor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-palette-{Guid.NewGuid():N}.cs");
        await File.WriteAllLinesAsync(path, new[] { "// あたま", "var x = 1;" });
        try
        {
            var content = await PalettePreviewLoader.LoadAsync(
                new PaletteTarget(path, 2), "a.cs", CancellationToken.None);

            // 言語が決まる拡張子ではトークンが付く（色そのものは描くとき UI スレッドで引く）。
            Assert.All(content.Lines, l => Assert.NotNull(l.Tokens));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_leaves_unknown_file_types_uncolored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-palette-{Guid.NewGuid():N}.unknownext");
        await File.WriteAllLinesAsync(path, new[] { "plain text" });
        try
        {
            var content = await PalettePreviewLoader.LoadAsync(
                new PaletteTarget(path), "a.unknownext", CancellationToken.None);

            Assert.All(content.Lines, l => Assert.Null(l.Tokens));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_reports_a_missing_file_rather_than_throwing()
    {
        var content = await PalettePreviewLoader.LoadAsync(
            new PaletteTarget(Path.Combine(Path.GetTempPath(), "loomo-palette-missing.txt")),
            "missing.txt", CancellationToken.None);

        Assert.Contains("見つかりません", content.Message);
    }
}
