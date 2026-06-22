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
using sk0ya.Loomo.Core.Tools.Implementations;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// <c>delegate_task</c> ツールと <see cref="SubAgentRunner"/> の振る舞い。
/// ツールは引数を正規化して <see cref="ISubAgentRunner"/> へ委譲し、結果を <see cref="ToolResult"/> へ写すだけ。
/// 実行器はまっさらな会話で 1 ターン回し、サブエージェントには <c>delegate_task</c> を提示しない（無限委譲防止）。
/// </summary>
public class DelegateTaskToolTests
{
    [Fact]
    public void Definition_exposes_task_required_and_does_not_require_approval()
    {
        var tool = new DelegateTaskTool(new FakeRunner());

        Assert.Equal("delegate_task", tool.Name);
        Assert.False(tool.RequiresApproval);   // 委譲自体は副作用なし

        var required = tool.Definition.InputSchema["required"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Contains(DelegateTaskContract.TaskArg, required);
        Assert.DoesNotContain(DelegateTaskContract.ContextArg, required);
    }

    [Fact]
    public void Normalize_maps_alias_keys_to_canonical_task_and_context()
    {
        var tool = new DelegateTaskTool(new FakeRunner());
        var raw = JsonSerializer.SerializeToElement(new { instruction = "やること", data = "入力" });

        var norm = tool.NormalizeArguments(raw);

        Assert.Equal("やること", norm.GetProperty(DelegateTaskContract.TaskArg).GetString());
        Assert.Equal("入力", norm.GetProperty(DelegateTaskContract.ContextArg).GetString());
    }

    [Fact]
    public void Normalize_omits_context_when_absent()
    {
        var tool = new DelegateTaskTool(new FakeRunner());
        var raw = JsonSerializer.SerializeToElement(new { task = "やること" });

        var norm = tool.NormalizeArguments(raw);

        Assert.Equal("やること", norm.GetProperty(DelegateTaskContract.TaskArg).GetString());
        Assert.False(norm.TryGetProperty(DelegateTaskContract.ContextArg, out _));
    }

    [Fact]
    public async Task Execute_empty_task_returns_recoverable_error_without_delegating()
    {
        var runner = new FakeRunner();
        var tool = new DelegateTaskTool(runner);
        var args = JsonSerializer.SerializeToElement(new { task = "   " });

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.False(runner.WasCalled);
    }

    [Fact]
    public async Task Execute_passes_task_and_context_and_returns_worker_text()
    {
        var runner = new FakeRunner { Result = new SubAgentResult("ワーカーの結果", IsError: false, new[] { "run_powershell" }) };
        var tool = new DelegateTaskTool(runner);
        var args = JsonSerializer.SerializeToElement(new { task = "集計して", context = "1,2,3" });

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("ワーカーの結果", result.Content);
        Assert.Equal("集計して", runner.LastTask);
        Assert.Equal("1,2,3", runner.LastContext);
    }

    [Fact]
    public async Task Execute_surfaces_worker_error_as_tool_error()
    {
        var runner = new FakeRunner { Result = new SubAgentResult("コマンド失敗", IsError: true, Array.Empty<string>()) };
        var tool = new DelegateTaskTool(runner);
        var args = JsonSerializer.SerializeToElement(new { task = "やる" });

        var result = await tool.ExecuteAsync(args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("コマンド失敗", result.Content);
    }

    [Fact]
    public async Task Runner_runs_fresh_conversation_and_hides_delegate_task_from_subagent()
    {
        // レジストリ: pwsh 相当 + delegate_task。実行器はサブへ delegate_task を提示しない。
        // AIクライアントは確定テキスト（TextDelta）を流し、TurnCompleted はオーケストレータが生成する。
        var ai = new ToolsRecordingAiClient(new AgentEvent[] { new TextDelta("サブ最終回答") });
        var registry = new ToolRegistry(new IAgentTool[]
        {
            new NamedDummyTool("run_powershell"),
            new NamedDummyTool(DelegateTaskContract.ToolName),
        });
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), registry, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);
        var provider = new StubProvider((typeof(AgentOrchestrator), orch), (typeof(ToolRegistry), registry));

        var runner = new SubAgentRunner(provider);
        var result = await runner.RunAsync("サブタスク", context: null, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("サブ最終回答", result.Text);

        // サブエージェントに提示されたツールに delegate_task は含まれない（無限委譲防止）。
        Assert.NotNull(ai.LastTools);
        Assert.Contains(ai.LastTools!, d => d.Name == "run_powershell");
        Assert.DoesNotContain(ai.LastTools!, d => d.Name == DelegateTaskContract.ToolName);
    }

    [Fact]
    public async Task Runner_maps_agent_error_to_error_result()
    {
        var ai = new ToolsRecordingAiClient(new AgentEvent[] { new AgentError("推論失敗") });
        var registry = new ToolRegistry(new IAgentTool[] { new NamedDummyTool("run_powershell") });
        var orch = new AgentOrchestrator(
            new FixedFactory(ai), registry, new AutoApprove(), new AllowAllSafety(),
            NoopContextWindowPolicy.Instance, NullLogger<AgentOrchestrator>.Instance);
        var provider = new StubProvider((typeof(AgentOrchestrator), orch), (typeof(ToolRegistry), registry));

        var result = await new SubAgentRunner(provider).RunAsync("やる", null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("推論失敗", result.Text);
    }

    // ===== フェイク =====

    private sealed class FakeRunner : ISubAgentRunner
    {
        public bool WasCalled { get; private set; }
        public string? LastTask { get; private set; }
        public string? LastContext { get; private set; }
        public SubAgentResult Result { get; set; } = new("", IsError: false, Array.Empty<string>());

        public Task<SubAgentResult> RunAsync(string task, string? context, CancellationToken ct)
        {
            WasCalled = true;
            LastTask = task;
            LastContext = context;
            return Task.FromResult(Result);
        }
    }

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
