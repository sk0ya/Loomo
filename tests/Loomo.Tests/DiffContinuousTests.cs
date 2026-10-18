using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.Tests;

/// <summary>差分を通しで読む（§24.50）。ファイルごとの行を見出しを挟んでつなぎ、どの行がどのファイルの何行目かを引ける。</summary>
public sealed class DiffContinuousTests
{
    private static DiffFileItem Item(string name)
        => new() { FullPath = @"C:\repo\" + name, DisplayPath = name, Badge = "M" };

    private static DiffSessionViewModel.ContinuousPart Part(DiffFileItem item, params DiffRowVm[] rows)
        => new(item, rows, rows.Select(_ => (Editor.Core.Syntax.SyntaxToken[]?)null).ToList(),
            rows.Select(_ => (IReadOnlyList<TextSpan>?)null).ToList());

    private static DiffRowVm Row(DiffLineKind kind, string text) => new(kind.ToString(), text);

    [Fact]
    public void 見出しを挟んでつなぎ_増減を数える()
    {
        var a = Item("a.cs");
        var b = Item("b.cs");

        var rows = DiffSessionViewModel.JoinForTests(
        [
            Part(a, Row(DiffLineKind.Gap, "@@ -1,2 +1,2 @@"), Row(DiffLineKind.Removed, "-x"), Row(DiffLineKind.Added, "+y")),
            Part(b, Row(DiffLineKind.Gap, "@@ -5,1 +5,2 @@"), Row(DiffLineKind.Context, " k"), Row(DiffLineKind.Added, "+z")),
        ], omittedFiles: 0);

        Assert.Equal(8, rows.Count);
        Assert.Equal("━━ a.cs　+1 −1", rows[0].Text);
        Assert.Equal("━━ b.cs　+1 −0", rows[4].Text);
        Assert.Same(a, rows[2].Owner);
        Assert.Same(b, rows[7].Owner);
        Assert.Equal(4, DiffSessionViewModel.ContinuousHeaderRow(rows, b));
    }

    [Fact]
    public void 行からそのファイルの範囲と行番号を引ける()
    {
        var a = Item("a.cs");
        var b = Item("b.cs");
        var rows = DiffSessionViewModel.JoinForTests(
        [
            Part(a, Row(DiffLineKind.Gap, "@@ -1,1 +1,1 @@"), Row(DiffLineKind.Added, "+y")),
            Part(b, Row(DiffLineKind.Gap, "@@ -10,2 +10,2 @@"), Row(DiffLineKind.Context, " k"), Row(DiffLineKind.Added, "+z")),
        ], omittedFiles: 0);

        var (owner, start, count) = DiffSessionViewModel.ContinuousSlice(rows, 6);

        Assert.Same(b, owner);
        Assert.Equal((4, 3), (start, count));
        Assert.Equal(11, DiffRowLineMapper.LineForUnifiedRow(rows.Skip(start).Take(count).ToList(), 6 - start, leftSide: false));
    }

    [Fact]
    public void 上限で打ち切ったら残りの数を出す()
    {
        var rows = DiffSessionViewModel.JoinForTests([Part(Item("a.cs"), Row(DiffLineKind.Added, "+y"))], omittedFiles: 3);

        Assert.Contains("残り 3 ファイル", rows[^1].Text);
        Assert.Null(rows[^1].Owner);
    }
}
