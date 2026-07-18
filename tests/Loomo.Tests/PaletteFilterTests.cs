using System;
using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>コマンドパレットの絞り込み（PaletteFilter）の検証。</summary>
public class PaletteFilterTests
{
    private static PaletteCommand Cmd(string category, string title)
        => new(category, title, () => { });

    private static readonly IReadOnlyList<PaletteCommand> Commands = new[]
    {
        Cmd("移動", "Editor へ"),
        Cmd("移動", "Terminal へ"),
        Cmd("ペイン", "Editor の表示を切替"),
        Cmd("タブ", "新しいターミナルタブ"),
        Cmd("コンポーザ", "本文をターミナルで実行"),
        Cmd("ワークスペース", "切替: Loomo"),
    };

    [Fact]
    public void Empty_query_returns_all_in_original_order()
    {
        var result = PaletteFilter.Filter(Commands, "  ");
        Assert.Equal(Commands.Select(c => c.Title), result.Select(c => c.Title));
    }

    [Fact]
    public void Title_prefix_ranks_above_contains()
    {
        var result = PaletteFilter.Filter(Commands, "Editor");

        // 名前が "Editor" で始まる2件が、カテゴリ等に含むだけのものより先に来る。
        Assert.Equal("Editor へ", result[0].Title);
        Assert.Equal("Editor の表示を切替", result[1].Title);
    }

    [Fact]
    public void All_tokens_must_match()
    {
        var result = PaletteFilter.Filter(Commands, "ターミナル 実行");

        var only = Assert.Single(result);
        Assert.Equal("本文をターミナルで実行", only.Title);
    }

    [Fact]
    public void Category_text_is_searchable()
    {
        var result = PaletteFilter.Filter(Commands, "ワークスペース");
        Assert.Contains(result, c => c.Title == "切替: Loomo");
    }

    [Fact]
    public void Subsequence_matches_when_no_substring()
    {
        // "Etr" は "Editor" 等の部分文字列ではないが、飛び石では一致する。
        var result = PaletteFilter.Filter(Commands, "Etr");
        Assert.NotEmpty(result);
        Assert.True(PaletteFilter.IsSubsequence("Etr", "Editor へ"));
    }

    [Fact]
    public void Unmatched_query_returns_empty()
        => Assert.Empty(PaletteFilter.Filter(Commands, "zzzz"));

    [Theory]
    [InlineData("abc", "a-b-c", true)]
    [InlineData("abc", "acb", false)]
    [InlineData("ABC", "abc", true)] // 大文字小文字は無視
    [InlineData("", "anything", true)]
    public void IsSubsequence_basics(string needle, string haystack, bool expected)
        => Assert.Equal(expected, PaletteFilter.IsSubsequence(needle, haystack));

    [Theory]
    [InlineData(null, PaletteMode.All, "")]
    [InlineData("hello", PaletteMode.All, "hello")]
    [InlineData("@ file.cs ", PaletteMode.File, "file.cs")]
    [InlineData("# text ", PaletteMode.Grep, "text")]
    [InlineData(": Type ", PaletteMode.Class, "Type")]
    [InlineData("% member ", PaletteMode.Symbol, "member")]
    [InlineData("$ output ", PaletteMode.Terminal, "output")]
    [InlineData("> command ", PaletteMode.Command, "command")]
    public void Palette_mode_is_parsed_without_UI(string? input, PaletteMode expectedMode, string expectedQuery)
    {
        var (mode, query) = CommandPaletteService.Parse(input);
        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedQuery, query);
    }

    [Fact]
    public void Palette_modes_cycle_in_display_order()
    {
        var modes = new List<PaletteMode>();
        var current = PaletteMode.All;
        for (var i = 0; i < 7; i++)
        {
            current = CommandPaletteService.Next(current);
            modes.Add(current);
        }

        Assert.Equal(new[]
        {
            PaletteMode.File, PaletteMode.Grep, PaletteMode.Class, PaletteMode.Symbol,
            PaletteMode.Terminal, PaletteMode.Command, PaletteMode.All
        }, modes);
        Assert.Equal("@", CommandPaletteService.Prefix(PaletteMode.File));
        Assert.Equal(string.Empty, CommandPaletteService.Prefix(PaletteMode.All));
    }
}
