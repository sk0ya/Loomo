using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 1ターンで複数ツールが要求されたとき（ストリーム検知でインライン実行）の挙動：
/// 配列順に実行され、結果が同順で1つの tool メッセージにまとまり、アシスタント直後に積まれること。
/// </summary>
public class OrchestratorMultiToolTests
{
    [Fact]
    public async Task Multiple_tool_uses_in_one_turn_run_in_array_order_and_round_trip()
    {
        var order = new List<string>();
        var tools = new ToolRegistry(new IAgentTool[]
        {
            new RecordingTool("toolA", order),
            new RecordingTool("toolB", order),
        });

        // 1ターン目で2ツールを要求 → 2ターン目で最終回答。
        var ai = new ScriptedAiClient(
            new AgentEvent[]
            {
                new ToolUseRequested(new ToolUse("id-a", "toolA", "{}")),
                new ToolUseRequested(new ToolUse("id-b", "toolB", "{}")),
            },
            new AgentEvent[] { new TextDelta("done") });

        var conv = new Conversation();
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var e in orch.RunTurnAsync(conv, "やって")) events.Add(e);

        // 配列順に実行された。
        Assert.Equal(new[] { "toolA", "toolB" }, order);

        // 実行イベントも順序どおり2件ずつ。
        Assert.Equal(new[] { "toolA", "toolB" },
            events.OfType<ToolExecutionStarted>().Select(e => e.ToolUse.Name).ToArray());
        Assert.Equal(new[] { "toolA", "toolB" },
            events.OfType<ToolExecutionCompleted>().Select(e => e.ToolUse.Name).ToArray());

        // 履歴: user → assistant(2 uses) → tool(2 results・同順) → assistant("done")。
        Assert.Equal(4, conv.Messages.Count);
        var assistant = conv.Messages[1];
        Assert.Equal(ChatRole.Assistant, assistant.Role);
        Assert.Equal(new[] { "toolA", "toolB" }, assistant.ToolUses.Select(u => u.Name).ToArray());

        var toolMsg = conv.Messages[2];
        Assert.Equal(ChatRole.Tool, toolMsg.Role);
        Assert.Equal(new[] { "id-a", "id-b" }, toolMsg.ToolResults.Select(r => r.ToolUseId).ToArray());

        Assert.Equal("done", Assert.Single(events.OfType<TurnCompleted>()).FinalText);
    }

    [Fact]
    public async Task Empty_tool_output_is_recorded_as_completed_result()
    {
        var tools = new ToolRegistry(new IAgentTool[] { new EmptyOutputTool() });
        var ai = new ScriptedAiClient(
            new AgentEvent[] { new ToolUseRequested(new ToolUse("id-empty", "empty_tool", "{}")) },
            new AgentEvent[] { new TextDelta("done") });

        var conv = new Conversation();
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var e in orch.RunTurnAsync(conv, "やって")) events.Add(e);

        var completed = Assert.Single(events.OfType<ToolExecutionCompleted>());
        Assert.Equal("tool completed: empty_tool (no output)", completed.Result.Content);
        Assert.False(completed.Result.IsError);

        var toolMsg = Assert.Single(conv.Messages, m => m.Role == ChatRole.Tool);
        var result = Assert.Single(toolMsg.ToolResults);
        Assert.Equal("id-empty", result.ToolUseId);
        Assert.Equal("tool completed: empty_tool (no output)", result.Content);
    }

    /// <summary>実行された名前を共有リストへ記録するだけのツール（副作用なし）。</summary>
    private sealed class RecordingTool : IAgentTool
    {
        private readonly List<string> _order;
        public RecordingTool(string name, List<string> order) { Name = name; _order = order; }

        public string Name { get; }
        public ToolDefinition Definition => new(Name, "recording test tool", new JsonObject());
        public bool RequiresApproval => false;
        public string DescribeInvocation(JsonElement arguments) => Name;

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
        {
            _order.Add(Name);
            return Task.FromResult(ToolResult.Ok($"ran {Name}"));
        }
    }

    private sealed class EmptyOutputTool : IAgentTool
    {
        public string Name => "empty_tool";
        public ToolDefinition Definition => new(Name, "returns no output", new JsonObject());
        public bool RequiresApproval => false;
        public string DescribeInvocation(JsonElement arguments) => Name;
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok(""));
    }

    private sealed class ScriptedAiClient : IAiClient
    {
        private readonly Queue<AgentEvent[]> _turns;
        public ScriptedAiClient(params AgentEvent[][] turns) => _turns = new Queue<AgentEvent[]>(turns);

        public AiProvider Provider => AiProvider.Local;

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Conversation conversation, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken ct, AgentProfile? profile = null,
            bool retryDiversify = false)
        {
            var events = _turns.Count > 0 ? _turns.Dequeue() : new AgentEvent[] { new TextDelta("") };
            foreach (var e in events)
            {
                await Task.CompletedTask;
                yield return e;
            }
        }
    }

    private sealed class FixedFactory : IAiClientFactory
    {
        private readonly IAiClient _client;
        public FixedFactory(IAiClient client) => _client = client;
        public IAiClient ResolveCurrent() => _client;
    }

    private sealed class AllowAllSafety : ISafetyPolicy
    {
        public bool AutoApprove => true;
        public SafetyDecision Evaluate(string toolName, JsonElement arguments) => SafetyDecision.Allow;
    }

    private sealed class AutoApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string summary, CancellationToken ct)
            => Task.FromResult(true);
    }
}
