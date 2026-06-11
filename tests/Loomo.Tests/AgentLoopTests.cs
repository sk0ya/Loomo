using System.Collections.Generic;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// エージェントループの堅牢化（コンテキスト長トリム / プロファイル適用）の検証。
/// </summary>
public class AgentLoopTests
{
    // ===== TokenEstimator =====

    [Fact]
    public void AgentProfile_appends_role_prompt_without_changing_base_prompt()
    {
        var prompt = AgentProfiles.Reviewer.ApplyTo("base prompt");

        Assert.StartsWith("base prompt", prompt);
        Assert.Contains("レビュー担当", prompt);
    }

    [Fact]
    public void AgentProfile_override_replaces_base_prompt()
    {
        var profile = new AgentProfile("custom", "Custom", SystemPromptOverride: "custom prompt");

        Assert.Equal("custom prompt", profile.ApplyTo("base prompt"));
    }

    [Fact]
    public void TokenEstimator_is_monotonic_with_length()
    {
        var shortMsg = new ChatMessage { Role = ChatRole.User, Text = "hi" };
        var longMsg = new ChatMessage { Role = ChatRole.User, Text = new string('x', 4000) };
        Assert.True(TokenEstimator.EstimateMessage(longMsg) > TokenEstimator.EstimateMessage(shortMsg));
    }

    // ===== ConversationTrimmer =====

    private static ChatMessage User(string text) => new() { Role = ChatRole.User, Text = text };

    private static ChatMessage Assistant(string text, params ToolUse[] uses)
    {
        var m = new ChatMessage { Role = ChatRole.Assistant, Text = text };
        m.ToolUses.AddRange(uses);
        return m;
    }

    private static ChatMessage Tool(params ToolResultMessage[] results)
    {
        var m = new ChatMessage { Role = ChatRole.Tool };
        m.ToolResults.AddRange(results);
        return m;
    }

    [Fact]
    public void Trim_returns_original_when_within_budget()
    {
        var msgs = new List<ChatMessage> { User("a"), Assistant("b") };
        var result = ConversationTrimmer.Trim(msgs, budgetTokens: 1000);
        Assert.Same(msgs, result);
    }

    [Fact]
    public void Trim_returns_original_when_budget_is_non_positive()
    {
        var msgs = new List<ChatMessage> { User(new string('x', 10000)) };
        Assert.Same(msgs, ConversationTrimmer.Trim(msgs, 0));
    }

    [Fact]
    public void Trim_drops_oldest_and_keeps_recent_within_budget()
    {
        var big = new string('x', 4000); // ≒1000トークン/メッセージ
        var msgs = new List<ChatMessage>
        {
            User(big),       // 0
            Assistant(big),  // 1
            User(big),       // 2
            Assistant(big),  // 3
            User("最新の質問"), // 4
        };

        var result = ConversationTrimmer.Trim(msgs, budgetTokens: 1200);

        Assert.True(result.Count < msgs.Count);
        Assert.Same(msgs[^1], result[^1]); // 最新メッセージは必ず残る
        Assert.Equal(ChatRole.User, result[0].Role); // 先頭は必ず user
    }

    [Fact]
    public void Trim_never_starts_with_orphan_tool_result()
    {
        // assistant(tool_use) → tool(result) のペアの「途中」で切れても、
        // 先頭が tool_result にならない（user まで前方に詰める）こと。
        var big = new string('x', 8000);
        var use = new ToolUse("id1", "run_powershell", "{}");
        var msgs = new List<ChatMessage>
        {
            User("古い"),                                            // 0
            Assistant(big, use),                                     // 1 (大きい assistant)
            Tool(new ToolResultMessage("id1", "result", false)),     // 2
            User("最新"),                                            // 3
        };

        var result = ConversationTrimmer.Trim(msgs, budgetTokens: 50);

        Assert.NotEqual(ChatRole.Tool, result[0].Role);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Same(msgs[^1], result[^1]);
    }
}
