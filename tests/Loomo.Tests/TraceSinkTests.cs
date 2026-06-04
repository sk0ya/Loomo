using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Observability;
using sk0ya.Loomo.Core.Safety;
using sk0ya.Loomo.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// AI操作トレース（観測性・設計書 §20）の検証。
/// JsonlTraceSink 単体と、AgentOrchestrator を通したエンドツーエンドの記録点を確認する。
/// </summary>
public class TraceSinkTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "loomo-trace-" + Guid.NewGuid().ToString("N"));

    private static List<TraceEvent> ReadEvents(string dir, string sessionId)
    {
        var path = Path.Combine(dir, sessionId + ".jsonl");
        var opts = new JsonSerializerOptions {PropertyNamingPolicy = JsonNamingPolicy.CamelCase};
        return File.ReadAllLines(path)
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<TraceEvent>(l, opts)!)
            .ToList();
    }

    [Fact]
    public async Task JsonlTraceSink_writes_one_line_per_event_with_monotonic_seq()
    {
        var dir = TempDir();
        var sink = new JsonlTraceSink(dir);

        sink.Record("s1", "t1", TraceKinds.TurnStarted, new {userInput = "hello"});
        sink.Record("s1", "t1", TraceKinds.AiMessage, new {fullText = "hi"});
        await sink.DisposeAsync(); // 残りをフラッシュ

        var events = ReadEvents(dir, "s1");
        Assert.Equal(2, events.Count);
        Assert.Equal(0, events[0].Seq);
        Assert.Equal(1, events[1].Seq);
        Assert.Equal(TraceKinds.TurnStarted, events[0].Kind);
        Assert.Equal("s1", events[0].SessionId);
        Assert.Equal("root", events[0].AgentId);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task JsonlTraceSink_keeps_seq_independent_per_session()
    {
        var dir = TempDir();
        var sink = new JsonlTraceSink(dir);

        sink.Record("a", null, TraceKinds.SessionStarted, null);
        sink.Record("b", null, TraceKinds.SessionStarted, null);
        sink.Record("a", "t", TraceKinds.AiMessage, new {fullText = "x"});
        await sink.DisposeAsync();

        Assert.Equal(new long[] {0, 1}, ReadEvents(dir, "a").Select(e => e.Seq));
        Assert.Equal(new long[] {0}, ReadEvents(dir, "b").Select(e => e.Seq));

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Orchestrator_records_full_lifecycle_to_trace()
    {
        var dir = TempDir();
        var sink = new JsonlTraceSink(dir);

        // 1ターン目でツールを1回呼び、2ターン目で最終テキストを返す台本付き AI。
        var tool = new EchoTool();
        var aiFactory = new ScriptedAiClientFactory(tool.Name);
        var orchestrator = new AgentOrchestrator(
            aiFactory,
            new ToolRegistry(new IAgentTool[] {tool}),
            new AutoApproveService(),
            new SafetyPolicy(new SafetySettings()),
            NoopContextWindowPolicy.Instance,
            NullLogger<AgentOrchestrator>.Instance,
            sink);

        var conversation = new Conversation();
        await foreach (var _ in orchestrator.RunTurnAsync(conversation, "やって", "sess1", CancellationToken.None))
        {
            // イベントは消費するだけ
        }

        await sink.DisposeAsync();

        var kinds = ReadEvents(dir, "sess1").Select(e => e.Kind).ToList();
        Assert.Contains(TraceKinds.SessionStarted, kinds);
        Assert.Contains(TraceKinds.TurnStarted, kinds);
        Assert.Contains(TraceKinds.AiToolUse, kinds);
        Assert.Contains(TraceKinds.SafetyEvaluated, kinds);
        Assert.Contains(TraceKinds.ToolStarted, kinds);
        Assert.Contains(TraceKinds.ToolCompleted, kinds);
        Assert.Contains(TraceKinds.AiMessage, kinds);
        Assert.Contains(TraceKinds.TurnCompleted, kinds);
        Assert.Equal(
            new[] {AgentProfiles.ChatUnderstanding.Id, AgentProfiles.ResultJudge.Id},
            aiFactory.Client.ProfileIds);
        Assert.Equal(new[] {1, 0}, aiFactory.Client.ToolCounts);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Orchestrator_reports_error_when_ai_stream_is_empty()
    {
        var dir = TempDir();
        var sink = new JsonlTraceSink(dir);
        var orchestrator = new AgentOrchestrator(
            new StaticAiClientFactory(new EmptyAiClient()),
            new ToolRegistry(Array.Empty<IAgentTool>()),
            new AutoApproveService(),
            new SafetyPolicy(new SafetySettings()),
            NoopContextWindowPolicy.Instance,
            NullLogger<AgentOrchestrator>.Instance,
            sink);

        var conversation = new Conversation();
        var events = new List<AgentEvent>();
        await foreach (var ev in orchestrator.RunTurnAsync(conversation, "こんにちは", "empty1", CancellationToken.None))
            events.Add(ev);
        await sink.DisposeAsync();

        var err = Assert.Single(events.OfType<AgentError>());
        Assert.Contains("応答が返りませんでした", err.Message);
        Assert.DoesNotContain(events, e => e is TurnCompleted);
        Assert.Contains(ReadEvents(dir, "empty1"), e => e.Kind == TraceKinds.Error);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task JsonlTraceSink_continues_seq_after_restart_for_resumed_session()
    {
        var dir = TempDir();

        // 1プロセス目：セッション "r" に2件記録。
        var sink1 = new JsonlTraceSink(dir);
        sink1.Record("r", "t1", TraceKinds.TurnStarted, null);
        sink1.Record("r", "t1", TraceKinds.AiMessage, new {fullText = "a"});
        await sink1.DisposeAsync();

        // 別プロセス相当：新しいシンクで同じセッションへ追記（再起動後のセッション再開）。
        var sink2 = new JsonlTraceSink(dir);
        sink2.Record("r", "t2", TraceKinds.TurnStarted, null);
        await sink2.DisposeAsync();

        // 0 が重複せず、ファイルの行数から続き番号が振られること。
        Assert.Equal(new long[] {0, 1, 2}, ReadEvents(dir, "r").Select(e => e.Seq));

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void JsonlTraceSink_sync_dispose_flushes_without_throwing()
    {
        var dir = TempDir();
        var sink = new JsonlTraceSink(dir);
        sink.Record("s", "t", TraceKinds.AiMessage, new {fullText = "x"});

        // ホスト終了経路（同期 IHost.Dispose）相当。IDisposable 実装で例外なくフラッシュされる。
        ((IDisposable) sink).Dispose();

        Assert.Single(ReadEvents(dir, "s"));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void NullTraceSink_records_nothing()
    {
        // 例外なく no-op であること（既定挙動の確認）。
        NullTraceSink.Instance.Record("s", "t", TraceKinds.AiMessage, new {x = 1});
    }

    // ===== テスト用の台本付き AI / ツール / サービス =====

    /// <summary>1ターン目で tool_use、2ターン目で最終テキストを返す。</summary>
    private sealed class ScriptedAiClient : IAiClient
    {
        private readonly string _toolName;
        private int _calls;

        public ScriptedAiClient(string toolName) => _toolName = toolName;

        public List<string> ProfileIds { get; } = new();
        public List<int> ToolCounts { get; } = new();

        public AiProvider Provider => AiProvider.Local;

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Conversation conversation,
            IReadOnlyList<ToolDefinition> tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct,
            AgentProfile? profile = null)
        {
            ProfileIds.Add(profile?.Id ?? "");
            ToolCounts.Add(tools.Count);
            await Task.Yield();
            if (_calls++ == 0)
            {
                yield return new ToolUseRequested(new ToolUse("u1", _toolName, "{\"value\":\"hi\"}"));
            }
            else
            {
                yield return new TextDelta("完了しました。");
            }
        }
    }

    private sealed class ScriptedAiClientFactory : IAiClientFactory
    {
        public ScriptedAiClientFactory(string toolName) => Client = new ScriptedAiClient(toolName);
        public ScriptedAiClient Client { get; }
        public IAiClient Resolve(AiProvider provider) => Client;
        public IAiClient ResolveCurrent() => Client;
    }

    private sealed class EmptyAiClient : IAiClient
    {
        public AiProvider Provider => AiProvider.Local;

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Conversation conversation,
            IReadOnlyList<ToolDefinition> tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct,
            AgentProfile? profile = null)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class StaticAiClientFactory : IAiClientFactory
    {
        private readonly IAiClient _client;
        public StaticAiClientFactory(IAiClient client) => _client = client;
        public IAiClient Resolve(AiProvider provider) => _client;
        public IAiClient ResolveCurrent() => _client;
    }

    private sealed class ContinueThenFinishAiClient : IAiClient
    {
        private int _calls;

        public List<string> ProfileIds { get; } = new();

        public AiProvider Provider => AiProvider.Local;

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Conversation conversation,
            IReadOnlyList<ToolDefinition> tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken ct,
            AgentProfile? profile = null)
        {
            ProfileIds.Add(profile?.Id ?? "");
            await Task.Yield();
            var call = _calls++;
            yield return new TextDelta(call switch
            {
                0 => "AI2に確認を依頼します。",
                1 => "追加情報が必要です。",
                2 => "[CONTINUE]\nもう一度整理してください。",
                3 => "AI2に最終確認を依頼します。",
                4 => "十分です。",
                _ => "完了しました。",
            });
        }
    }

    private sealed class EchoTool : IAgentTool
    {
        public string Name => "echo";
        public bool RequiresApproval => false;
        public ToolDefinition Definition => new(Name, "echo", ToolDefinition.ObjectSchema());
        public string DescribeInvocation(JsonElement arguments) => Name;

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
            => Task.FromResult(ToolResult.Ok("echoed"));
    }

    private sealed class AutoApproveService : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string summary, CancellationToken ct)
            => Task.FromResult(true);
    }
}