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
/// <see cref="SubAgentRunner"/>：ツールが内部で AI を活用するための基盤。まっさらな会話で
/// <see cref="AgentOrchestrator.RunTurnAsync"/> を 1 ターン回し、最終テキスト（またはエラー）を取り出す。
/// </summary>
public class SubAgentRunnerTests
{
    [Fact]
    public async Task Runs_fresh_conversation_and_returns_final_text()
    {
        // AIクライアントは確定テキスト（TextDelta）を流し、TurnCompleted はオーケストレータが生成する。
        var ai = new ToolsRecordingAiClient(new AgentEvent[] { new TextDelta("サブ最終回答") });
        var registry = new ToolRegistry(new IAgentTool[] { new NamedDummyTool("run_powershell") });
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), registry, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);
        var provider = new StubProvider((typeof(AgentOrchestrator), orch));

        var result = await new SubAgentRunner(provider).RunAsync("サブタスク", context: null, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("サブ最終回答", result.Text);
        // サブエージェントには全登録ツールが提示される（委譲ツールという特別扱いは無い）。
        Assert.NotNull(ai.LastTools);
        Assert.Contains(ai.LastTools!, d => d.Name == "run_powershell");
    }

    [Fact]
    public async Task Maps_agent_error_to_error_result()
    {
        var ai = new ToolsRecordingAiClient(new AgentEvent[] { new AgentError("推論失敗") });
        var registry = new ToolRegistry(new IAgentTool[] { new NamedDummyTool("run_powershell") });
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), registry, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);
        var provider = new StubProvider((typeof(AgentOrchestrator), orch));

        var result = await new SubAgentRunner(provider).RunAsync("やる", null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("推論失敗", result.Text);
    }

    [Fact]
    public async Task Passes_context_into_subagent_prompt()
    {
        var ai = new ToolsRecordingAiClient(new AgentEvent[] { new TextDelta("ok") });
        var registry = new ToolRegistry(new IAgentTool[] { new NamedDummyTool("run_powershell") });
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), registry, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);
        var provider = new StubProvider((typeof(AgentOrchestrator), orch));

        await new SubAgentRunner(provider).RunAsync("要約して", context: "本文データ", CancellationToken.None);

        // サブ会話の最初の user メッセージに task と context が両方含まれる。
        var firstUser = ai.LastConversation!.Messages.First(m => m.Role == ChatRole.User).Text ?? "";
        Assert.Contains("要約して", firstUser);
        Assert.Contains("本文データ", firstUser);
    }

    // ===== フェイク =====

    private sealed class StubProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _map;
        public StubProvider(params (Type, object)[] items) => _map = items.ToDictionary(x => x.Item1, x => x.Item2);
        public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var v) ? v : null;
    }

    private sealed class NamedDummyTool : IAgentTool
    {
        public NamedDummyTool(string name) => Name = name;
        public string Name { get; }
        public ToolDefinition Definition => new(Name, Name, new JsonObject());
        public bool RequiresApproval => false;
        public string DescribeInvocation(JsonElement arguments) => Name;
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok("ran"));
    }

    private sealed class ToolsRecordingAiClient : IAiClient
    {
        private readonly AgentEvent[] _events;
        public IReadOnlyList<ToolDefinition>? LastTools { get; private set; }
        public Conversation? LastConversation { get; private set; }
        public ToolsRecordingAiClient(AgentEvent[] events) => _events = events;
        public AiProvider Provider => AiProvider.Local;

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Conversation conversation, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken ct, AgentProfile? profile = null,
            bool retryDiversify = false)
        {
            LastTools = tools;
            LastConversation = conversation;
            foreach (var e in _events)
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
