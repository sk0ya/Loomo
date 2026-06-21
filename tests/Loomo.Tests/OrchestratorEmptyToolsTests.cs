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
/// <c>toolDefinitionsOverride</c> に空配列を渡すと、登録ツールがあってもモデルへは1件も提示されず、
/// 1回のAI応答で即終端すること。通常のチャット／ワークフロー実行は override を渡さず登録ツールを使う。
/// </summary>
public class OrchestratorEmptyToolsTests
{
    [Fact]
    public async Task Empty_override_passes_no_tools_to_model_and_completes_in_one_turn()
    {
        // レジストリにはツールが居るが、override で空にする。
        var tools = new ToolRegistry(new IAgentTool[] { new DummyTool() });
        var ai = new ToolsRecordingAiClient(new AgentEvent[] { new TextDelta("結果テキスト") });

        var conv = new Conversation();
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var e in orch.RunTurnAsync(
                           conv, "やって", toolDefinitionsOverride: System.Array.Empty<ToolDefinition>()))
            events.Add(e);

        // モデルへ提示されたツールは0件。
        Assert.NotNull(ai.LastTools);
        Assert.Empty(ai.LastTools!);

        // 1回のAI応答で終端（ツールループしない）。
        var completed = Assert.Single(events.OfType<TurnCompleted>());
        Assert.Equal("結果テキスト", completed.FinalText);

        // 履歴は user → assistant のみ（tool メッセージは無い）。
        Assert.Equal(2, conv.Messages.Count);
        Assert.Equal(ChatRole.User, conv.Messages[0].Role);
        Assert.Equal(ChatRole.Assistant, conv.Messages[1].Role);
    }

    [Fact]
    public async Task Default_override_passes_registered_tools()
    {
        var tools = new ToolRegistry(new IAgentTool[] { new DummyTool() });
        var ai = new ToolsRecordingAiClient(new AgentEvent[] { new TextDelta("ok") });

        var conv = new Conversation();
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);

        await foreach (var _ in orch.RunTurnAsync(conv, "やって")) { }

        // override 未指定なら全登録ツールが提示される。
        Assert.NotNull(ai.LastTools);
        Assert.Single(ai.LastTools!);
    }

    [Fact]
    public async Task Turn_preamble_is_set_on_user_message_render_prefix_without_polluting_text()
    {
        var tools = new ToolRegistry(new IAgentTool[] { new DummyTool() });
        var ai = new ToolsRecordingAiClient(new AgentEvent[] { new TextDelta("ok") });

        var conv = new Conversation();
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);

        await foreach (var _ in orch.RunTurnAsync(conv, "やって", turnPreamble: "MODE-X")) { }

        var user = conv.Messages[0];
        Assert.Equal(ChatRole.User, user.Role);
        // 追加プロンプトは RenderPrefix にのみ載り、保存・履歴に残る Text は素のまま。
        Assert.Equal("MODE-X", user.RenderPrefix);
        Assert.Equal("やって", user.Text);
    }

    [Fact]
    public async Task No_preamble_leaves_render_prefix_null()
    {
        var tools = new ToolRegistry(new IAgentTool[] { new DummyTool() });
        var ai = new ToolsRecordingAiClient(new AgentEvent[] { new TextDelta("ok") });

        var conv = new Conversation();
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), tools, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);

        await foreach (var _ in orch.RunTurnAsync(conv, "やって")) { }

        Assert.Null(conv.Messages[0].RenderPrefix);
    }

    private sealed class DummyTool : IAgentTool
    {
        public string Name => "dummy";
        public ToolDefinition Definition => new(Name, "dummy", new JsonObject());
        public bool RequiresApproval => false;
        public string DescribeInvocation(JsonElement arguments) => Name;
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok("ran"));
    }

    private sealed class ToolsRecordingAiClient : IAiClient
    {
        private readonly AgentEvent[] _events;
        public IReadOnlyList<ToolDefinition>? LastTools { get; private set; }

        public ToolsRecordingAiClient(AgentEvent[] events) => _events = events;

        public AiProvider Provider => AiProvider.Local;

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Conversation conversation, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken ct, AgentProfile? profile = null,
            bool retryDiversify = false)
        {
            LastTools = tools;
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
        public IAiClient Resolve(AiProvider provider) => _client;
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
