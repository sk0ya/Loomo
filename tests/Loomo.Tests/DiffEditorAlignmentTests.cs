using System.Linq;
using Editor.Core.Editing;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 左右並び差分を左右2つのエディタへ載せる組み替え。行 i がどちらのエディタでも表示行 i に来ること
/// （＝片側にしか無い行のぶん反対側へ空き行が入ること）が、スクロール連動・中央の帯・次/前の変更の前提。
/// </summary>
public class DiffEditorAlignmentTests
{
    private static DiffSideRowVm Context(int left, int right, string text)
        => new("Context", text, "Context", text, left.ToString(), right.ToString());

    private static DiffSideRowVm Removed(int left, string text)
        => new("Removed", text, "Empty", "", left.ToString(), "");

    private static DiffSideRowVm Added(int right, string text)
        => new("Empty", "", "Added", text, "", right.ToString());

    private static DiffSideRowVm Changed(int left, string oldText, int right, string newText)
        => new("Removed", oldText, "Added", newText, left.ToString(), right.ToString());

    /// <summary>エディタに載せたときの表示行数（本文の行＋空き行）。</summary>
    private static int VisualRows(DiffEditorSide side, int editorLineCount)
        => editorLineCount + side.Decorations.SpacersBefore.Values.Sum();

    [Fact]
    public void 片側にしか無い行のぶん反対側へ空き行が入り行の数が揃う()
    {
        var rows = new[]
        {
            Context(1, 1, "a"),
            Removed(2, "b"),
            Removed(3, "c"),
            Context(4, 2, "d"),
            Added(3, "e"),
        };

        var (left, right) = DiffEditorAlignment.Build(rows);

        Assert.Equal(new[] { "a", "b", "c", "d" }, left.Lines);
        Assert.Equal(new[] { "a", "d", "e" }, right.Lines);
        // 右は d（行1）の前に b・c のぶん2行、左は末尾に e のぶん1行。
        Assert.Equal(2, right.Decorations.SpacersBefore[1]);
        Assert.Equal(1, left.Decorations.SpacersBefore[4]);
        Assert.Equal(rows.Length, VisualRows(left, 4));
        Assert.Equal(rows.Length, VisualRows(right, 3));
    }

    [Fact]
    public void 追加と削除の行に背景の種別が付く()
    {
        var rows = new[] { Context(1, 1, "a"), Changed(2, "old", 2, "new"), Added(3, "more") };

        var (left, right) = DiffEditorAlignment.Build(rows);

        Assert.Equal(DiffDecorationKind.Removed, left.Decorations.Lines[1]);
        Assert.False(left.Decorations.Lines.ContainsKey(0));
        Assert.Equal(DiffDecorationKind.Added, right.Decorations.Lines[1]);
        Assert.Equal(DiffDecorationKind.Added, right.Decorations.Lines[2]);
    }

    /// <summary>エディタは末尾改行を「空の最終行」として持つ（git の行には無い）。そのぶん反対側の
    /// 末尾に空き行を足さないと、最後の方で左右が1行ずれる。</summary>
    [Fact]
    public void 末尾改行の空行ぶん反対側の末尾に空き行を足す()
    {
        var rows = new[] { Context(1, 1, "a"), Context(2, 2, "b") };

        // 右はファイルを開いたエディタ（"a\nb\n" → 3行）、左は行から作った文書（2行）。
        var (left, right) = DiffEditorAlignment.Build(rows, leftEditorLineCount: 2, rightEditorLineCount: 3);

        Assert.Equal(1, left.Decorations.SpacersBefore[2]);
        Assert.Empty(right.Decorations.SpacersBefore);
        Assert.Equal(VisualRows(left, 2), VisualRows(right, 3));
    }

    [Fact]
    public void 両側とも末尾改行で終わるなら足さない()
    {
        var rows = new[] { Context(1, 1, "a") };

        var (left, right) = DiffEditorAlignment.Build(rows, leftEditorLineCount: 2, rightEditorLineCount: 2);

        Assert.Empty(left.Decorations.SpacersBefore);
        Assert.Empty(right.Decorations.SpacersBefore);
    }

    /// <summary>新規ファイル（左に行が1つも無い）。空の文書もエディタでは1行あるので、その1行ぶん右の末尾を足す。</summary>
    [Fact]
    public void 新規ファイルは左が空き行だけになり高さが揃う()
    {
        var rows = new[] { Added(1, "x"), Added(2, "y") };

        var (left, right) = DiffEditorAlignment.Build(rows);

        Assert.Empty(left.Lines);
        Assert.Equal(2, left.Decorations.SpacersBefore[0]);
        Assert.Equal(1, right.Decorations.SpacersBefore[2]);
        Assert.Equal(VisualRows(left, 1), VisualRows(right, 2));
    }

    [Fact]
    public void 編集中の本文で行を取り直すと末尾改行は行に数えない()
    {
        var rows = DiffEditorAlignment.Recompute(["a", "b"], "a\nB\nc\n");

        Assert.Equal(new[] { "a", "b" }, DiffEditorAlignment.SideLines(rows).Left);
        Assert.Equal(new[] { "a", "B", "c" }, DiffEditorAlignment.SideLines(rows).Right);
        Assert.Contains(rows, row => row.RightKind == "Added" && row.RightText == "c");
    }

    [Theory]
    [InlineData("a\nb", true)]
    [InlineData("a\nb\n", true)]
    [InlineData("a\r\nb\r\n", true)]
    [InlineData("a\nb\n\n", false)]
    [InlineData("a\nc", false)]
    public void エディタの本文と行の本文の一致は末尾改行1つを同じとみなす(string editorText, bool expected)
        => Assert.Equal(expected, DiffEditorAlignment.SameText(["a", "b"], editorText));

    /// <summary>git の出力は UTF-8 として読むので BOM 付きファイルの1行目に BOM が残る。エディタは剥がして
    /// 読むので、残すと1行目が常に「違う」になり、保存前の編集として取り直され続ける。</summary>
    [Fact]
    public void 一行目のBOMは本文から外す()
    {
        var rows = new[] { Context(1, 1, "﻿using System;"), Context(2, 2, "x") };

        var (left, right) = DiffEditorAlignment.SideLines(rows);

        Assert.Equal("using System;", left[0]);
        Assert.True(DiffEditorAlignment.SameText(right, "using System;\nx\n"));
    }

    [Fact]
    public void 行の添字とバッファ行を行き来できる()
    {
        var rows = new[] { Context(1, 1, "a"), Removed(2, "b"), Context(3, 2, "c") };

        Assert.Equal(2, DiffEditorAlignment.RowOfLine(rows, bufferLine: 1, left: false));
        Assert.Equal(1, DiffEditorAlignment.RowOfLine(rows, bufferLine: 1, left: true));
        // 右に無い行（削除行）からは、右の直前の行へ。
        Assert.Equal(0, DiffEditorAlignment.LineOfRow(rows, 1, left: false));
        Assert.Equal(1, DiffEditorAlignment.LineOfRow(rows, 1, left: true));
        Assert.Equal(-1, DiffEditorAlignment.RowOfLine(rows, bufferLine: 9, left: true));
    }

    /// <summary>§24.70 左右並びでも、移動したブロックの行に印を付ける（左＝移動元・右＝移動先）。</summary>
    [Fact]
    public void Moved_block_is_marked_on_both_sides()
    {
        var method = new[] { "int Compute(int value)", "{", "    return value * Factor + Offset;" };
        var rows = new List<DiffSideRowVm>
        {
            Removed(1, method[0]), Removed(2, method[1]), Removed(3, method[2]),
            Context(4, 1, "void Run() { }"),
            Added(2, method[0]), Added(3, method[1]), Added(4, method[2]),
        };

        var (left, right) = DiffEditorAlignment.Build(rows);

        Assert.Equal(new[] { 0, 1, 2 }, left.Decorations.MovedLines.Keys.OrderBy(k => k));
        Assert.All(left.Decorations.MovedLines.Values, Assert.True);
        Assert.Equal(new[] { 1, 2, 3 }, right.Decorations.MovedLines.Keys.OrderBy(k => k));
        Assert.All(right.Decorations.MovedLines.Values, Assert.False);
    }

    /// <summary>同じ高さで書き換えた行（その場の字下げ直し）は移動にしない。</summary>
    [Fact]
    public void Rewriting_in_place_is_not_a_move()
    {
        var rows = new List<DiffSideRowVm>
        {
            Changed(1, "int Compute(int value)", 1, "  int Compute(int value)"),
            Changed(2, "{", 2, "  {"),
            Changed(3, "    return value * Factor + Offset;", 3, "      return value * Factor + Offset;"),
        };

        var (left, right) = DiffEditorAlignment.Build(rows);

        Assert.Empty(left.Decorations.MovedLines);
        Assert.Empty(right.Decorations.MovedLines);
    }
}
