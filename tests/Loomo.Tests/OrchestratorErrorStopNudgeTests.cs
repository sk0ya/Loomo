using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 虚偽成功ガード：ツール失敗の直後にモデルがツールを呼ばず最終回答で終わろうとしたら、
/// 1ターンに1回だけ差し戻して「修正して再実行」か「正直な失敗報告」を求める。
/// 小モデルがエラー後に「完了しました」と虚偽報告する主要残存故障への決定論的対策。
/// </summary>
public class OrchestratorErrorStopNudgeTests
{
    [Fact]
    public async Task Final_answer_right_after_tool_error_is_nudged_once_then_completes()
    {
        var ai = new ScriptedAiClient(
            new AgentEvent[] { new ToolUseRequested(new ToolUse("t1", "always_fail", "{}")) },
            new AgentEvent[] { new TextDelta("コピーしました。") },          // 虚偽成功 → 差し戻し
            new AgentEvent[] { new TextDelta("失敗を確認しました。") });     // 正直な報告 → 終端
        var conv = new Conversation();

        var events = await Collect(Build(ai).RunTurnAsync(conv, "やって"));

        Assert.Equal(3, ai.Calls);   // ツール → 虚偽成功（差し戻し）→ 再回答
        Assert.Equal("失敗を確認しました。", Assert.Single(events.OfType<TurnCompleted>()).FinalText);
        Assert.Contains(conv.Messages,
            m => m.Role == ChatRole.User && m.Text!.Contains("完了済みとして報告してはいけません"));
    }

    [Fact]
    public async Task Nudge_fires_only_once_per_turn()
    {
        var ai = new ScriptedAiClient(
            new AgentEvent[] { new ToolUseRequested(new ToolUse("t1", "always_fail", "{}")) },
            new AgentEvent[] { new TextDelta("完了しました。") },   // 差し戻し1回目
            new AgentEvent[] { new TextDelta("完了しました。") });  // 2回目はそのまま終端（押し問答しない）
        var conv = new Conversation();

        var events = await Collect(Build(ai).RunTurnAsync(conv, "やって"));

        Assert.Equal(3, ai.Calls);
        Assert.Single(events.OfType<TurnCompleted>());
        Assert.Equal(1, conv.Messages.Count(
            m => m.Role == ChatRole.User && m.Text!.Contains("完了済みとして報告してはいけません")));
    }

    [Fact]
    public async Task Final_answer_after_successful_tool_is_not_nudged()
    {
        var ai = new ScriptedAiClient(
            new AgentEvent[] { new ToolUseRequested(new ToolUse("t1", "always_ok", "{}")) },
            new AgentEvent[] { new TextDelta("できました。") });
        var conv = new Conversation();

        var events = await Collect(Build(ai).RunTurnAsync(conv, "やって"));

        Assert.Equal(2, ai.Calls);   // ツール → 最終回答（差し戻し無し）
        Assert.Equal("できました。", Assert.Single(events.OfType<TurnCompleted>()).FinalText);
        Assert.DoesNotContain(conv.Messages,
            m => m.Role == ChatRole.User && m.Text!.Contains("完了済みとして報告してはいけません"));
    }

    [Fact]
    public async Task Error_then_successful_retry_then_final_answer_is_not_nudged()
    {
        var ai = new ScriptedAiClient(
            new AgentEvent[] { new ToolUseRequested(new ToolUse("t1", "always_fail", "{}")) },
            new AgentEvent[] { new ToolUseRequested(new ToolUse("t2", "always_ok", "{}")) },
            new AgentEvent[] { new TextDelta("やり直して成功しました。") });
        var conv = new Conversation();

        var events = await Collect(Build(ai).RunTurnAsync(conv, "やって"));

        Assert.Equal(3, ai.Calls);
        Assert.Equal("やり直して成功しました。", Assert.Single(events.OfType<TurnCompleted>()).FinalText);
        // 直前の反復は成功しているので差し戻さない（失敗→自力回復→報告は正常系）。
        Assert.DoesNotContain(conv.Messages,
            m => m.Role == ChatRole.User && m.Text!.Contains("完了済みとして報告してはいけません"));
    }

    private static AgentOrchestrator Build(IAiClient ai) => new(
        new FixedFactory(ai),
        new ToolRegistry(new IAgentTool[] { new FixedTool("always_fail", fail: true), new FixedTool("always_ok", fail: false) }),
        new AutoApprove(),
        new AllowAllSafety(),
        NoopContextWindowPolicy.Instance,
        NullLogger<AgentOrchestrator>.Instance);

    private static async Task<List<AgentEvent>> Collect(IAsyncEnumerable<AgentEvent> stream)
    {
        var list = new List<AgentEvent>();
        await foreach (var e in stream) list.Add(e);
        return list;
    }

    /// <summary>常に成功／失敗する固定ツール。</summary>
    private sealed class FixedTool : IAgentTool
    {
        private readonly bool _fail;
        public FixedTool(string name, bool fail) { Name = name; _fail = fail; }
        public string Name { get; }
        public bool RequiresApproval => false;
        public ToolDefinition Definition => new(Name, "fixed", ToolDefinition.ObjectSchema());
        public string DescribeInvocation(JsonElement args) => Name;
        public JsonElement NormalizeArguments(JsonElement arguments) => arguments;
        public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct)
            => Task.FromResult(_fail ? ToolResult.Error("わざと失敗") : ToolResult.Ok("OK"));
    }

    private sealed class ScriptedAiClient : IAiClient
    {
        private readonly Queue<AgentEvent[]> _turns;
        public int Calls { get; private set; }
        public ScriptedAiClient(params AgentEvent[][] turns) => _turns = new Queue<AgentEvent[]>(turns);
        public AiProvider Provider => AiProvider.Local;

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Conversation conversation, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken ct, AgentProfile? profile = null,
            bool retryDiversify = false)
        {
            Calls++;
            var events = _turns.Count > 0 ? _turns.Dequeue() : new AgentEvent[] { new TurnCompleted("") };
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
