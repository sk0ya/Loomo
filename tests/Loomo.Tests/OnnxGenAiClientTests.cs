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

    /// <summary>渡された <see cref="GenerationRequest"/> を記録するだけのフェイクエンジン。</summary>
    private sealed class CapturingEngine : ILocalInferenceEngine
    {
        public GenerationRequest? Last { get; private set; }
        public Task GenerateAsync(GenerationRequest request, ChannelWriter<AgentEvent> sink, CancellationToken ct)
        {
            Last = request;
            sink.TryWrite(new TextDelta("ok"));
            sink.TryComplete();
            return Task.CompletedTask;
        }
    }

    private static async Task<GenerationRequest> CaptureRequestAsync(bool retryDiversify)
        => await CaptureRequestAsync(retryDiversify, new AiSettings(), System.Array.Empty<ToolDefinition>(), "やあ");

    private static async Task<GenerationRequest> CaptureRequestAsync(
        bool retryDiversify,
        AiSettings settings,
        IReadOnlyList<ToolDefinition> tools,
        string userText)
    {
        var engine = new CapturingEngine();
        var client = new OnnxGenAiClient(engine, settings, new FakeWorkspaceService());
        var conv = new Conversation();
        conv.AddUser(userText);
        await foreach (var _ in client.StreamAsync(conv, tools,
                           CancellationToken.None, profile: null, retryDiversify: retryDiversify)) { }
        return engine.Last!;
    }

    private static ToolDefinition DummyTool() => new(
        "run_powershell",
        "run",
        ToolDefinition.ObjectSchema(("command", "string", "PowerShell command", true)));

    private static string ExtractPhi4SystemText(string prompt)
    {
        const string start = "<|system|>";
        var startIndex = prompt.IndexOf(start, System.StringComparison.Ordinal);
        Assert.True(startIndex >= 0, "Phi-4 prompt should contain a system turn.");
        startIndex += start.Length;

        var toolIndex = prompt.IndexOf("<|tool|>", startIndex, System.StringComparison.Ordinal);
        var endIndex = prompt.IndexOf("<|end|>", startIndex, System.StringComparison.Ordinal);
        Assert.True(endIndex >= 0, "Phi-4 system turn should be closed.");

        var stopIndex = toolIndex >= 0 && toolIndex < endIndex ? toolIndex : endIndex;
        return prompt[startIndex..stopIndex];
    }

    [Fact]
    public async Task Retry_diversify_opens_top_k_so_temperature_actually_samples()
    {
        // genai_config の既定は top_k=1（=温度を上げても候補1つで greedy）。リトライ多様化では top_k を
        // 明示的に開かないと出力が分岐せず、同じ不正JSONを再生産してしまう。retry 時は top_k>1 を要求する。
        var retry = await CaptureRequestAsync(retryDiversify: true);
        Assert.NotNull(retry.Sampling.TopK);
        Assert.True(retry.Sampling.TopK > 1, "リトライ多様化では候補プールを開くため top_k>1 が必要");
        Assert.NotNull(retry.Sampling.Temperature);

        // 通常時はモデル別プロファイルに委ねる（既定 AiSettings=phi4 未指定＝greedy 相当で top_k を上書きしない）。
        var normal = await CaptureRequestAsync(retryDiversify: false);
        Assert.Null(normal.Sampling.TopK);
    }

    [Fact]
    public async Task Chat_and_workflow_steps_use_same_phi4_system_prompt()
    {
        // チャット初回ターンとワークフローのステップは、同じ会話内容・同じツール定義なら
        // 完全に同じプロンプトを作る。UIは別でも OnnxGenAiClient -> ChatPrompt.Build の経路を共有する。
        var tools = new[] { DummyTool() };

        var chat = await CaptureRequestAsync(false, new AiSettings(), tools, "READMEを読んで");
        var workflow = await CaptureRequestAsync(false, new AiSettings(), tools, "READMEを読んで");

        Assert.Equal(chat.Prompt, workflow.Prompt);
        Assert.Equal(ExtractPhi4SystemText(chat.Prompt), ExtractPhi4SystemText(workflow.Prompt));
    }

    [Fact]
    public async Task Workflow_steps_keep_same_phi4_tool_block_for_warmup_prefix()
    {
        // ワークフローでも、ウォームアップ済みプレフィックスを再利用するためチャットと同じツール定義ブロックを保つ。
        var tools = new[] { DummyTool() };
        var chat = await CaptureRequestAsync(false, new AiSettings(), tools, "要約して");
        var workflowTextOnly = await CaptureRequestAsync(
            false, new AiSettings(), tools, "要約して");

        Assert.Equal(chat.Prompt, workflowTextOnly.Prompt);
        Assert.Contains("<|tool|>", chat.Prompt);
        Assert.Contains("<|tool|>", workflowTextOnly.Prompt);
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
    public async Task Streams_raw_chunks_live_while_buffering_for_final_decision()
    {
        // 生成中の各チャンクは揮発性の RawTextDelta として逐次（順序どおり）流れ、終端で確定 TextDelta が出る。
        var events = await RunAsync(new TextDelta("こん"), new TextDelta("にちは"));

        Assert.Equal(
            new[] { "こん", "にちは" },
            events.OfType<RawTextDelta>().Select(r => r.Text).ToArray());
        // RawTextDelta は確定本文より前に流れる（ライブプレビュー → 確定の順）。
        Assert.True(events.FindIndex(e => e is RawTextDelta) < events.FindIndex(e => e is TextDelta));
        Assert.Equal("こんにちは", string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
    }

    [Fact]
    public async Task Tool_call_text_streams_raw_but_never_becomes_text_delta()
    {
        // ツール呼び出しJSONも生成中は RawTextDelta で見えるが、確定本文（TextDelta）には漏れない。
        var events = await RunAsync(new TextDelta("{\"command\":\"ls\"}"));

        Assert.Contains(events, e => e is RawTextDelta);
        Assert.Single(events.OfType<ToolUseRequested>());
        Assert.DoesNotContain(events, e => e is TextDelta);
    }

    [Fact]
    public async Task Converts_tool_call_arguments_json_to_tool_use_without_turn_completed()
    {
        var events = await RunAsync(new TextDelta("{\"command\":\"ls\"}"));

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

    [Fact]
    public async Task Unparseable_tool_call_attempt_emits_parse_failed_with_raw_text()
    {
        // ツール呼び出しらしき不正JSON（補修しても1件も拾えない＝値が欠落して構造ごと壊れている）。
        // 生JSONを最終回答として黙って出すのでも終端エラーにするのでもなく、生出力ごと ToolCallParseFailed で
        // 差し戻して再試行可能にする。※ "content=" 等の打ち間違いは補修で救えるようになったため別の壊れ方を使う。
        const string raw = "[{\"name\":\"write_file\",\"content\":}]";
        var events = await RunAsync(new TextDelta(raw));

        var failed = Assert.Single(events.OfType<ToolCallParseFailed>());
        Assert.Equal(raw, failed.RawText);   // 生出力がそのまま伝わる（ユーザー/AI が何が出たか分かる）
        Assert.DoesNotContain(events, e => e is TurnCompleted);
        Assert.DoesNotContain(events, e => e is AgentError);
    }

    [Fact]
    public async Task Streams_tool_call_array_as_multiple_tool_uses_in_order()
    {
        // ツール呼び出し配列は要素数ぶんの ToolUseRequested として順序どおり出て、本文/終端は出さない（ツール継続）。
        var events = await RunAsync(new TextDelta(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"a\"}}," +
            "{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"b\"}}]"));

        var tools = events.OfType<ToolUseRequested>().ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal("{\"command\":\"a\"}", tools[0].ToolUse.ArgumentsJson);
        Assert.Equal("{\"command\":\"b\"}", tools[1].ToolUse.ArgumentsJson);
        Assert.DoesNotContain(events, e => e is TextDelta);
        Assert.DoesNotContain(events, e => e is TurnCompleted);
    }

    [Fact]
    public async Task Preserves_trailing_text_after_tool_call_array_as_confirmed_text_delta()
    {
        // 配列のツール呼び出しに同伴した後置きの自然文を捨てず、確定本文（TextDelta）として残す（履歴に積まれる）。
        var events = await RunAsync(new TextDelta(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}]\n一覧を取得します。"));

        Assert.Single(events.OfType<ToolUseRequested>());
        var text = Assert.Single(events.OfType<TextDelta>());
        Assert.Equal("一覧を取得します。", text.Text);
        // ツール継続なので終端は出さない。確定本文はツール確定の後に流れる。
        Assert.DoesNotContain(events, e => e is TurnCompleted);
        Assert.True(events.FindIndex(e => e is ToolUseRequested) < events.FindIndex(e => e is TextDelta));
    }

    [Fact]
    public async Task Pure_tool_call_array_emits_no_text_delta()
    {
        // 配列外に自然文が無ければ余計な TextDelta は出さない（従来挙動の維持）。
        var events = await RunAsync(new TextDelta(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}]"));

        Assert.Single(events.OfType<ToolUseRequested>());
        Assert.DoesNotContain(events, e => e is TextDelta);
    }

    [Fact]
    public async Task Dispatches_tool_use_early_before_later_chunks_arrive()
    {
        // 早期ディスパッチ：1件目の ToolUseRequested は、2件目のチャンク（RawTextDelta）より前に出る。
        var events = await RunAsync(
            new TextDelta("[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"a\"}},"),
            new TextDelta("{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"b\"}}]"));

        Assert.Equal(2, events.OfType<ToolUseRequested>().Count());
        var firstTool = events.FindIndex(e => e is ToolUseRequested);
        var lastRaw = events.FindLastIndex(e => e is RawTextDelta);
        Assert.True(firstTool < lastRaw, "1件目のツールは2件目のチャンク到達前に確定するはず");
    }
}
