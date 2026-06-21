using sk0ya.Loomo.Core.Agent;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>ステップ指示文のプレースホルダ置換（前段出力の受け渡し）の検証。</summary>
public class WorkflowPromptTests
{
    [Fact]
    public void Numbered_placeholder_is_replaced_with_that_step_output()
    {
        var result = WorkflowPrompt.Resolve("要約: {{1}}", new[] { "本文A", "本文B" });
        Assert.Equal("要約: 本文A", result);
    }

    [Fact]
    public void Prev_resolves_to_last_output()
    {
        var result = WorkflowPrompt.Resolve("続き: {{prev}}", new[] { "X", "Y", "Z" });
        Assert.Equal("続き: Z", result);
    }

    [Fact]
    public void All_joins_every_previous_output_with_headers()
    {
        var result = WorkflowPrompt.Resolve("{{all}}", new[] { "一", "二" });
        Assert.Contains("【ステップ1の出力】", result);
        Assert.Contains("【ステップ2の出力】", result);
        Assert.Contains("一", result);
        Assert.Contains("二", result);
    }

    [Fact]
    public void Out_of_range_number_becomes_empty()
    {
        var result = WorkflowPrompt.Resolve("[{{3}}]", new[] { "only-one" });
        Assert.Equal("[]", result);
    }

    [Fact]
    public void Prev_with_no_previous_output_becomes_empty()
    {
        var result = WorkflowPrompt.Resolve("[{{prev}}]", System.Array.Empty<string>());
        Assert.Equal("[]", result);
    }

    [Fact]
    public void No_placeholder_leaves_prompt_unchanged_and_does_not_auto_append()
    {
        var result = WorkflowPrompt.Resolve("そのままの指示", new[] { "前段の出力" });
        Assert.Equal("そのままの指示", result);
    }

    [Theory]
    [InlineData("{{ 1 }}")]
    [InlineData("{{PREV}}")]
    public void Whitespace_and_case_are_tolerated(string token)
    {
        var result = WorkflowPrompt.Resolve(token, new[] { "値" });
        Assert.Equal("値", result);
    }
}
