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
/// 不正なツール呼び出しJSON（<see cref="ToolCallParseFailed"/>）の回復挙動：
/// 終端エラーにせず誤りを差し戻して AI に再試行させ、上限超過でのみ生出力を添えて打ち切る。
/// </summary>
public class OrchestratorParseRetryTests
{
    [Fact]
    public async Task Parse_failure_then_valid_text_retries_and_completes()
    {
        var ai = new ScriptedAiClient(
            new AgentEvent[] { new ToolCallParseFailed("[{\"name\":\"write_file\",\"content=\"x\"}]") },
            new AgentEvent[] { new TextDelta("直しました") });
        var conv = new Conversation();

        var events = await Collect(Build(ai).RunTurnAsync(conv, "やって"));

        Assert.Equal(2, ai.Calls);                                   // 1回失敗 → 1回再試行
        Assert.Contains(events, e => e is ToolCallParseFailed);      // 生出力は通知される（可視化）
        Assert.Equal("直しました", Assert.Single(events.OfType<TurnCompleted>()).FinalText);
        // 誤りを伝える補正メッセージが会話へ差し込まれている（AI が自己修正できる）。
        Assert.Contains(conv.Messages, m => m.Role == ChatRole.User && m.Text!.Contains("解釈できませんでした"));
    }

    [Fact]
    public async Task Repeated_parse_failures_give_up_with_raw_output_in_error()
    {
        var raw = new AgentEvent[] { new ToolCallParseFailed("[BAD-JSON]") };
        var ai = new ScriptedAiClient(raw, raw, raw, raw);   // 何度出しても不正
        var conv = new Conversation();

        var events = await Collect(Build(ai).RunTurnAsync(conv, "やって"));

        Assert.Equal(3, ai.Calls);                                   // 初回＋再試行2回で打ち切り
        var err = Assert.Single(events.OfType<AgentError>());
        Assert.Contains("[BAD-JSON]", err.Message);                  // 何が出たかをエラーに残す
        Assert.DoesNotContain(events, e => e is TurnCompleted);
    }

    private static AgentOrchestrator Build(IAiClient ai) => new(
        new FixedFactory(ai),
        new ToolRegistry(Array.Empty<IAgentTool>()),
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

    /// <summary>呼び出しごとに次のイベント列を返すフェイク AI クライアント。</summary>
    private sealed class ScriptedAiClient : IAiClient
    {
        private readonly Queue<AgentEvent[]> _turns;
        public int Calls { get; private set; }

        public ScriptedAiClient(params AgentEvent[][] turns) => _turns = new Queue<AgentEvent[]>(turns);

        public AiProvider Provider => AiProvider.Local;

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Conversation conversation, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken ct, AgentProfile? profile = null)
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
