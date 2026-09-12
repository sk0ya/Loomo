using sk0ya.Loomo.Ai.Completion;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 入力の先読み（FIM）のうち、モデルを呼ばない部分。プロンプトの組み方と、
/// 返ってきた候補を出してよいかの判断。
/// </summary>
public sealed class FimPromptTests
{
    private static readonly string[] Lines =
    [
        "public sealed class Sample",     // 0
        "{",                              // 1
        "    private int _count;",        // 2
        "",                               // 3
        "    public void Add(int n)",     // 4
        "    {",                          // 5
        "        _count += n;",           // 6
        "    }",                          // 7
        "}",                              // 8
    ];

    [Fact]
    public void The_caret_splits_the_text_into_prefix_and_suffix()
    {
        // 行 6 の桁 14（"        _count" の直後）にキャレット。前 2 行＋後ろ 1 行を見せる。
        var prompt = FimPrompt.Build(Lines, line: 6, column: 14, prefixLines: 2, suffixLines: 1);

        Assert.Equal(
            FimPrompt.PrefixToken + "    public void Add(int n)\n    {\n        _count" +
            FimPrompt.SuffixToken + " += n;\n    }" +
            FimPrompt.MiddleToken,
            prompt);
    }

    [Fact]
    public void Line_counts_are_clamped_to_the_buffer()
    {
        var prompt = FimPrompt.Build(Lines, line: 0, column: 0, prefixLines: 50, suffixLines: 50);

        Assert.StartsWith(FimPrompt.PrefixToken + FimPrompt.SuffixToken, prompt, StringComparison.Ordinal);
        Assert.EndsWith("}" + FimPrompt.MiddleToken, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99, 0)]
    [InlineData(2, 999)]
    public void Out_of_range_positions_do_not_throw(int line, int column)
    {
        var prompt = FimPrompt.Build(Lines, line, column, 3, 3);
        Assert.Contains(FimPrompt.MiddleToken, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_buffer_still_produces_a_well_formed_prompt()
    {
        Assert.Equal(
            FimPrompt.PrefixToken + FimPrompt.SuffixToken + FimPrompt.MiddleToken,
            FimPrompt.Build([], 0, 0, 10, 10));
    }
}

/// <summary>
/// 候補を落とす側。実測した 0.5B は「形は正しいが中身が違う」候補を平気で出すので、
/// ここで落とせるもの（文字列として壊れているもの）は確実に落とす。
/// </summary>
public sealed class FimCandidateFilterTests
{
    private static string? Clean(string raw, string before = "        return ", string after = "")
        => FimCandidateFilter.Clean(raw, before, after);

    [Fact]
    public void A_plain_continuation_passes_through()
        => Assert.Equal("result.Count;", Clean("result.Count;"));

    [Fact]
    public void Only_the_first_line_is_kept()
        => Assert.Equal("result.Count;", Clean("result.Count;\n    }\n}"));

    [Theory]
    [InlineData("resultValue;<|endoftext|>")]
    [InlineData("resultValue;<|file_sep|>")]
    [InlineData("resultValue;<|fim_prefix|>more")]
    public void Everything_after_a_stop_marker_is_dropped(string raw)
        => Assert.Equal("resultValue;", Clean(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void Nothing_comes_of_an_empty_generation(string raw)
        => Assert.Null(Clean(raw));

    /// <summary>トークン境界の都合で、モデルが手掛かりごと書き直してくることがある。
    /// そのまま入れると "returnreturn result" になる。</summary>
    [Fact]
    public void A_candidate_that_repeats_the_clue_is_rejected()
        => Assert.Null(Clean("return result.Count;"));

    [Fact]
    public void A_candidate_that_only_repeats_what_follows_is_rejected()
        => Assert.Null(Clean("result.Count;", "        return ", "result.Count;"));

    [Theory]
    [InlineData(");")]
    [InlineData("}")]
    [InlineData("_")]
    public void Punctuation_only_fragments_are_not_worth_showing(string raw)
        => Assert.Null(Clean(raw));

    [Fact]
    public void A_short_candidate_with_two_words_is_still_useful()
        => Assert.Equal("ics(e);", Clean("ics(e);"));

    [Theory]
    [InlineData("aaaaaaaaaaaa;")]
    [InlineData("x = y y y y;")]
    public void Runaway_repetition_is_rejected(string raw)
        => Assert.Null(Clean(raw));

    [Fact]
    public void A_candidate_that_closes_more_than_it_opens_is_rejected()
        => Assert.Null(Clean("value));", "        Write(x"));

    [Fact]
    public void A_candidate_that_leaves_a_string_open_is_rejected()
        => Assert.Null(Clean("\"unterminated value;"));

    /// <summary>元から対応が取れていない行（複数行に渡る式の途中）は、そのままでよい。
    /// 見たいのは「候補を入れたせいで壊れたか」だけ。</summary>
    [Fact]
    public void An_already_unbalanced_line_is_not_blamed_on_the_candidate()
        => Assert.Equal("items.Select(x =>", Clean("items.Select(x =>", "        var q = "));

    /// <summary>行コメントの中まで括弧や引用符を数えると、ふつうの日本語・英語が書けなくなる。</summary>
    [Theory]
    [InlineData("// don't count this", "        x = 1; ")]
    [InlineData("// 閉じ括弧 ) について", "        x = 1; ")]
    public void Text_inside_a_line_comment_is_not_counted(string raw, string before)
        => Assert.Equal(raw, Clean(raw, before));

    [Fact]
    public void Control_characters_never_reach_the_screen()
        => Assert.Null(Clean("resultValue\u0007Count;"));

    [Fact]
    public void Trailing_whitespace_is_trimmed()
        => Assert.Equal("result.Count;", Clean("result.Count;   "));
}
