using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// <see cref="OnnxGenAiClient"/> の終端処理（本文バッファ→ツール判定／TurnCompleted／usage 透過）を、
/// 実モデル不要のフェイクエンジンで検証する。
/// </summary>
public class OnnxGenAiClientTests
{
    /// <summary>スクリプトしたイベントをそのままチャネルへ流すフェイク推論エンジン。</summary>
    private sealed class FakeInferenceEngine : ILocalInferenceEngine
    {
        private readonly AgentEvent[] _events;
        public FakeInferenceEngine(params AgentEvent[] events) => _events = events;

        public Task GenerateAsync(GenerationRequest request, ChannelWriter<AgentEvent> sink, CancellationToken ct)
        {
            foreach (var e in _events) sink.TryWrite(e);
            sink.TryComplete();
            return Task.CompletedTask;
        }
    }

    private static async Task<List<AgentEvent>> RunAsync(params AgentEvent[] scripted)
    {
        var client = new OnnxGenAiClient(
            new FakeInferenceEngine(scripted), new AiSettings(), new FakeWorkspaceService());
        var conv = new Conversation();
        conv.AddUser("やあ");

        var events = new List<AgentEvent>();
        await foreach (var ev in client.StreamAsync(
                           conv,
                           new[] { new ToolDefinition("run_powershell", "run", ToolDefinition.ObjectSchema(
                               ("command", "string", "PowerShell command", true))) },
                           CancellationToken.None))
            events.Add(ev);
        return events;
    }

    [Fact]
    public async Task Buffers_text_and_emits_single_delta_then_turn_completed()
    {
        var events = await RunAsync(new TextDelta("こんにちは"), new TextDelta("！"));

        Assert.Equal("こんにちは！", string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
        var done = Assert.Single(events.OfType<TurnCompleted>());
        Assert.Equal("こんにちは！", done.FinalText);
        Assert.DoesNotContain(events, e => e is ToolUseRequested);
    }

    [Fact]
    public async Task Converts_tool_call_json_array_to_tool_use_without_turn_completed()
    {
        var events = await RunAsync(new TextDelta(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}]"));

        var tool = Assert.Single(events.OfType<ToolUseRequested>());
        Assert.Equal("run_powershell", tool.ToolUse.Name);
        Assert.Equal("{\"command\":\"ls\"}", tool.ToolUse.ArgumentsJson);
        Assert.DoesNotContain(events, e => e is TurnCompleted);   // ツール継続のため出さない
        Assert.DoesNotContain(events, e => e is TextDelta);
    }

    [Fact]
    public async Task Forwards_usage_event()
    {
        var usage = new AiUsageReported(100, 20, 1500, 800, 200, 2500);
        var events = await RunAsync(new TextDelta("ok"), usage);

        Assert.Same(usage, Assert.Single(events.OfType<AiUsageReported>()));
    }

    [Fact]
    public async Task Forwards_engine_error_and_stops()
    {
        var events = await RunAsync(new AgentError("ローカル推論に失敗しました: boom"));

        Assert.Contains(events, e => e is AgentError { Message: "ローカル推論に失敗しました: boom" });
        Assert.DoesNotContain(events, e => e is TurnCompleted);
    }

    [Fact]
    public async Task Reports_error_when_no_model_output()
    {
        var events = await RunAsync();   // 何も生成しない

        var err = Assert.Single(events.OfType<AgentError>());
        Assert.Contains("応答本文が返りませんでした", err.Message);
        Assert.DoesNotContain(events, e => e is TurnCompleted);
    }
}
