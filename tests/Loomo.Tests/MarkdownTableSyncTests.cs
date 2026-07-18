using System.Linq;
using sk0ya.Loomo.Core.Markdown;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// <see cref="MarkdownTableSync"/> の検出・パース・再生成の検証。
/// （グリッドウィンドウ自体は UI 依存のためここでは扱わない。）
/// </summary>
public class MarkdownTableSyncTests
{
    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void FindsTable_WhenCaretInsideBody()
    {
        var lines = Lines(
            "前書き\n" +
            "\n" +
            "| 名前 | 年齢 |\n" +
            "|------|-----:|\n" +
            "| 太郎 | 30 |\n" +
            "| 花子 | 25 |\n" +
            "\n" +
            "後書き");

        Assert.True(MarkdownTableSync.TryFindTableAt(lines, caretLine: 4, out var region));
        Assert.Equal(2, region.StartLine);   // ヘッダ行
        Assert.Equal(5, region.EndLine);     // 最終本文行
        Assert.Equal(3, region.Rows.Count);  // ヘッダ + 本文2
        Assert.Equal(new[] { "名前", "年齢" }, region.Rows[0]);
        Assert.Equal(new[] { "太郎", "30" }, region.Rows[1]);
        Assert.Equal(MarkdownColumnAlignment.None, region.Alignments[0]);
        Assert.Equal(MarkdownColumnAlignment.Right, region.Alignments[1]);
    }

    [Fact]
    public void FindsTable_WhenCaretOnHeaderOrSeparator()
    {
        var lines = Lines("| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.True(MarkdownTableSync.TryFindTableAt(lines, 0, out _));
        Assert.True(MarkdownTableSync.TryFindTableAt(lines, 1, out _));
    }

    [Fact]
    public void ReturnsFalse_WhenNoSeparatorRow()
    {
        // 区切り行が無い ＝ GFM テーブルではない。
        var lines = Lines("| a | b |\n| 1 | 2 |");
        Assert.False(MarkdownTableSync.TryFindTableAt(lines, 0, out _));
    }

    [Fact]
    public void ReturnsFalse_WhenCaretOnPlainLine()
    {
        var lines = Lines("ただの文\n| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.False(MarkdownTableSync.TryFindTableAt(lines, 0, out _));
    }

    [Fact]
    public void ParsesEscapedPipe()
    {
        var lines = Lines("| a | b |\n|---|---|\n| x \\| y | 2 |");
        Assert.True(MarkdownTableSync.TryFindTableAt(lines, 2, out var region));
        Assert.Equal("x | y", region.Rows[1][0]);
    }

    [Fact]
    public void Serialize_RoundTripsAndEscapes()
    {
        var rows = new[]
        {
            new[] { "名前", "メモ" },
            new[] { "太郎", "a | b" },
        };
        var aligns = new[] { MarkdownColumnAlignment.Left, MarkdownColumnAlignment.Center };

        var table = MarkdownTableSync.SerializeTable(rows, aligns);
        var outLines = table.Split('\n');

        Assert.Equal(3, outLines.Length);              // ヘッダ + 区切り + 本文1
        Assert.StartsWith("| :", outLines[1]);         // 左揃えマーカー
        Assert.Contains(":", outLines[1].Split('|')[2]); // 中央揃えは両端コロン
        Assert.Contains("a \\| b", table);             // '|' がエスケープされている

        // 生成物を読み直すと元の値へ戻る。
        var reparsed = Lines(table);
        Assert.True(MarkdownTableSync.TryFindTableAt(reparsed, 2, out var region));
        Assert.Equal("a | b", region.Rows[1][1]);
    }

    [Fact]
    public void InsertTableAt_AfterNonBlankLine_AddsBlankSeparators()
    {
        var lines = Lines("前の段落\n次の段落");
        var result = MarkdownTableSync.InsertTableAt(lines, caretLine: 0, "| a |\n|---|");

        Assert.Equal(new[] { "前の段落", "", "| a |", "|---|", "", "次の段落" }, result);
    }

    [Fact]
    public void InsertTableAt_OnBlankLine_ReplacesIt()
    {
        var lines = Lines("前の段落\n\n次の段落");
        var result = MarkdownTableSync.InsertTableAt(lines, caretLine: 1, "| a |\n|---|");

        // 空行のカーソル行はテーブルに置き換わり、前後に空行が補われる。
        Assert.Equal(new[] { "前の段落", "", "| a |", "|---|", "", "次の段落" }, result);
    }

    [Fact]
    public void InsertTableAt_EmptyDocument_InsertsTableOnly()
    {
        var result = MarkdownTableSync.InsertTableAt(Lines(""), caretLine: 0, "| a |\n|---|");
        Assert.Equal(new[] { "| a |", "|---|" }, result);
    }

    [Fact]
    public void InsertTableAt_AtLastLine_NoTrailingBlank()
    {
        var result = MarkdownTableSync.InsertTableAt(Lines("末尾の行"), caretLine: 0, "| a |\n|---|");
        Assert.Equal(new[] { "末尾の行", "", "| a |", "|---|" }, result);
    }

    [Fact]
    public void Serialize_DropsTrailingEmptyRowsAndColumns()
    {
        // グリッドの余白（末尾の空行・空列）は出力しない。
        var rows = new[]
        {
            new[] { "a", "b", "" },
            new[] { "1", "2", "" },
            new[] { "", "", "" },
        };
        var table = MarkdownTableSync.SerializeTable(rows, new[] { MarkdownColumnAlignment.None });
        var outLines = table.Split('\n');

        Assert.Equal(3, outLines.Length);          // 空行は消える
        Assert.DoesNotContain("| c |", table);
        // 各行が2列（末尾空列が落ちている）: パイプは 3 本。
        Assert.Equal(3, outLines[0].Count(ch => ch == '|'));
    }
}
