using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Tests;

public class OllamaClientTests
{
    [Fact]
    public void BuildRequest_without_tools_omits_tool_fields_and_flattens_tool_history()
    {
        var conversation = new Conversation();
        conversation.AddUser("ビルドして");
        var assistant = new ChatMessage { Role = ChatRole.Assistant, Text = "確認します" };
        assistant.ToolUses.Add(new ToolUse("t1", "run_command", "{\"command\":\"dotnet build\"}"));
        conversation.Messages.Add(assistant);
        var tool = new ChatMessage { Role = ChatRole.Tool };
        tool.ToolResults.Add(new ToolResultMessage("t1", "成功", IsError: false));
        conversation.Messages.Add(tool);

        var body = OllamaProtocol.BuildRequest(
            conversation,
            new[] { new ToolDefinition("run_command", "run", ToolDefinition.ObjectSchema()) },
            "gemma3:4b",
            1024,
            "system",
            includeTools: false);

        Assert.False(body.ContainsKey("tools"));

        var messages = body["messages"]!.AsArray();
        Assert.DoesNotContain(messages, m => m?["role"]?.GetValue<string>() == "tool");
        Assert.DoesNotContain(messages, m => m?["tool_calls"] is not null);
        Assert.Contains(messages, m => m?["content"]?.GetValue<string>().Contains("過去のツール実行結果") == true);
    }

    [Fact]
    public void BuildRequest_with_tools_serializes_arguments_as_object_and_tags_tool_results()
    {
        var conversation = new Conversation();
        conversation.AddUser("ビルドして");
        var assistant = new ChatMessage { Role = ChatRole.Assistant, Text = "" };
        assistant.ToolUses.Add(new ToolUse("t1", "run_command", "{\"command\":\"dotnet build\"}"));
        conversation.Messages.Add(assistant);
        var tool = new ChatMessage { Role = ChatRole.Tool };
        tool.ToolResults.Add(new ToolResultMessage("t1", "成功", IsError: false));
        conversation.Messages.Add(tool);

        var body = OllamaProtocol.BuildRequest(
            conversation,
            new[] { new ToolDefinition("run_command", "run", ToolDefinition.ObjectSchema()) },
            "qwen3:4b",
            1024,
            "system");

        Assert.True(body.ContainsKey("tools"));
        var messages = body["messages"]!.AsArray();

        var asMsg = messages.Single(m => m?["role"]?.GetValue<string>() == "assistant")!;
        var args = asMsg["tool_calls"]![0]!["function"]!["arguments"];
        Assert.IsType<JsonObject>(args);                       // 文字列ではなくオブジェクト
        Assert.Equal("dotnet build", args!["command"]!.GetValue<string>());

        var toolMsg = messages.Single(m => m?["role"]?.GetValue<string>() == "tool")!;
        Assert.Equal("run_command", toolMsg["tool_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Local_client_retries_without_tools_when_ollama_model_rejects_tools()
    {
        var handler = new ScriptedHandler();
        using var http = new HttpClient(handler);
        // 未知モデル（プロファイル既定で tools を試行する）を使い、サーバ拒否時のフォールバックを検証する。
        // 既知の tools 非対応モデル（gemma3 等）は最初からツールを送らないため別経路になる。
        var settings = new AiSettings
        {
            Local = new ProviderConfig { Model = "mistral:7b", BaseUrl = "http://localhost:11434", MaxTokens = 1024 }
        };
        var client = new OllamaClient(http, settings, new FakeWorkspaceService());
        var conversation = new Conversation();
        conversation.AddUser("こんにちは");

        var events = new List<AgentEvent>();
        await foreach (var ev in client.StreamAsync(
                           conversation,
                           new[] { new ToolDefinition("run_command", "run", ToolDefinition.ObjectSchema()) },
                           CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Contains(events, e => e is TextDelta { Text: "通常チャットで回答します。" });
        Assert.DoesNotContain(events, e => e is AgentError);
        Assert.Equal(2, handler.PostedBodies.Count);
        Assert.True(handler.PostedBodies[0].ContainsKey("tools"));
        Assert.False(handler.PostedBodies[1].ContainsKey("tools"));
    }

    [Fact]
    public async Task Local_client_disables_think_when_thinking_is_off()
    {
        var body = await CapturePostedBodyAsync(thinking: false);
        Assert.False(body["think"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Local_client_enables_think_when_thinking_is_on()
    {
        var body = await CapturePostedBodyAsync(thinking: true);
        Assert.True(body["think"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Local_client_surfaces_thinking_field_separately_from_content()
    {
        var events = await RunLocalAsync(
            Ndjson("{\"message\":{\"role\":\"assistant\",\"thinking\":\"まず手順を\",\"content\":\"\"},\"done\":false}"),
            Ndjson("{\"message\":{\"role\":\"assistant\",\"thinking\":\"整理する。\",\"content\":\"\"},\"done\":false}"),
            Ndjson("{\"message\":{\"role\":\"assistant\",\"thinking\":\"\",\"content\":\"こんにちは！\"},\"done\":true}"));

        var thinking = string.Concat(events.OfType<ThinkingDelta>().Select(t => t.Text));
        var text = string.Concat(events.OfType<TextDelta>().Select(t => t.Text));
        Assert.Equal("まず手順を整理する。", thinking);
        Assert.Equal("こんにちは！", text);
    }

    [Fact]
    public async Task Local_client_reports_error_when_stream_has_no_model_output()
    {
        var events = await RunLocalAsync(
            Ndjson("{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}"));

        var err = Assert.Single(events.OfType<AgentError>());
        Assert.Contains("応答本文が返りませんでした", err.Message);
        Assert.DoesNotContain(events, e => e is TurnCompleted);
    }

    [Fact]
    public async Task Local_client_emits_tool_call_with_object_arguments()
    {
        var events = await RunLocalAsync(
            Ndjson("{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"run_command\",\"arguments\":{\"command\":\"ls\"}}}]},\"done\":true}"));

        var tool = Assert.Single(events.OfType<ToolUseRequested>());
        Assert.Equal("run_command", tool.ToolUse.Name);
        Assert.Equal("{\"command\":\"ls\"}", tool.ToolUse.ArgumentsJson);
        Assert.DoesNotContain(events, e => e is TurnCompleted); // ツール継続のため出さない
    }

    [Fact]
    public void BuildRequest_applies_qwen3_thinking_sampling_and_num_ctx()
    {
        var conversation = new Conversation();
        conversation.AddUser("考えて");

        var body = OllamaProtocol.BuildRequest(
            conversation, Array.Empty<ToolDefinition>(), "qwen3:4b", 1024, "system",
            includeTools: true, wantThink: true);

        Assert.True(body["think"]!.GetValue<bool>());
        var options = body["options"]!.AsObject();
        Assert.Equal(32768, options["num_ctx"]!.GetValue<int>());
        Assert.Equal(0.6, options["temperature"]!.GetValue<double>());     // thinking 時の推奨温度
        Assert.Equal(0.95, options["top_p"]!.GetValue<double>());
        Assert.Equal(20, options["top_k"]!.GetValue<int>());
    }

    [Fact]
    public void BuildRequest_uses_qwen3_non_thinking_sampling_when_think_disabled()
    {
        var conversation = new Conversation();
        conversation.AddUser("即答して");

        var body = OllamaProtocol.BuildRequest(
            conversation, Array.Empty<ToolDefinition>(), "qwen3:4b", 1024, "system",
            includeTools: true, wantThink: false);

        Assert.False(body["think"]!.GetValue<bool>());
        var options = body["options"]!.AsObject();
        Assert.Equal(0.7, options["temperature"]!.GetValue<double>());     // 非 thinking 時の推奨温度
        Assert.Equal(0.8, options["top_p"]!.GetValue<double>());
    }

    [Fact]
    public void BuildRequest_sends_think_false_for_non_thinking_model_even_when_requested()
    {
        var conversation = new Conversation();
        conversation.AddUser("やあ");

        // gemma3 は thinking 非対応。think を要求されても true は送らず、無害な think:false にする
        // （think:true はエラー、think:false は全モデルで無害で既定オンの未知モデルも黙らせられる）。
        var body = OllamaProtocol.BuildRequest(
            conversation, Array.Empty<ToolDefinition>(), "gemma3:4b", 1024, "system",
            includeTools: true, wantThink: true);

        Assert.False(body["think"]!.GetValue<bool>());
        Assert.Equal(1.0, body["options"]!["temperature"]!.GetValue<double>());
    }

    [Fact]
    public void BuildRequest_num_ctx_override_takes_precedence_over_profile()
    {
        var conversation = new Conversation();
        conversation.AddUser("やあ");

        var body = OllamaProtocol.BuildRequest(
            conversation, Array.Empty<ToolDefinition>(), "qwen3:4b", 1024, "system",
            includeTools: true, wantThink: false, numCtxOverride: 4096);

        Assert.Equal(4096, body["options"]!["num_ctx"]!.GetValue<int>());   // プロファイル既定(32768)ではなく上書き値
    }

    [Fact]
    public void BuildRequest_omits_tools_for_tool_unsupported_model_despite_includeTools()
    {
        var conversation = new Conversation();
        conversation.AddUser("ビルドして");

        // gemma3 は tools 非対応。includeTools:true でもツールは送らない。
        var body = OllamaProtocol.BuildRequest(
            conversation,
            new[] { new ToolDefinition("run_command", "run", ToolDefinition.ObjectSchema()) },
            "gemma3:4b", 1024, "system", includeTools: true);

        Assert.False(body.ContainsKey("tools"));
    }

    [Theory]
    [InlineData("qwen3:4b", true, true)]
    [InlineData("qwen3:0.6b", true, true)]
    [InlineData("qwen2.5:3b", true, false)]
    [InlineData("qwen2.5-coder:3b", true, false)]
    [InlineData("gemma3:4b", false, false)]
    [InlineData("phi4-mini:3.8b", true, false)]
    [InlineData("phi4-mini", true, false)]
    [InlineData("some-unknown-model:7b", true, false)]
    public void Resolve_maps_installed_models_to_expected_capabilities(
        string model, bool tools, bool thinking)
    {
        var profile = ModelProfiles.Resolve(model);
        Assert.Equal(tools, profile.SupportsTools);
        Assert.Equal(thinking, profile.SupportsThinking);
    }

    [Theory]
    [InlineData("phi4-mini:3.8b", true)]
    [InlineData("qwen3:4b", false)]
    public async Task System_prompt_carries_conciseness_guidance_only_for_phi4_mini(
        string model, bool expectGuidance)
    {
        var body = await CapturePostedBodyForModelAsync(model);
        var system = body["messages"]!.AsArray()
            .First(m => m?["role"]?.GetValue<string>() == "system")!["content"]!.GetValue<string>();

        Assert.Equal(expectGuidance, system.Contains("応答スタイル"));
    }

    private static string Ndjson(string jsonLine) => jsonLine + "\n";

    private static async Task<JsonObject> CapturePostedBodyForModelAsync(string model)
    {
        var handler = new RecordingHandler(
            Ndjson("{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}"));
        using var http = new HttpClient(handler);
        var settings = new AiSettings
        {
            Local = new ProviderConfig
            {
                Model = model,
                BaseUrl = "http://localhost:11434",
                MaxTokens = 1024
            }
        };
        var client = new OllamaClient(http, settings, new FakeWorkspaceService());
        var conversation = new Conversation();
        conversation.AddUser("これは何？");

        await foreach (var _ in client.StreamAsync(conversation, Array.Empty<ToolDefinition>(), CancellationToken.None))
        {
        }

        return Assert.Single(handler.PostedBodies);
    }

    private static async Task<JsonObject> CapturePostedBodyAsync(bool thinking)
    {
        var handler = new RecordingHandler(
            Ndjson("{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}"));
        using var http = new HttpClient(handler);
        var settings = new AiSettings
        {
            Local = new ProviderConfig
            {
                Model = "qwen3:4b",
                BaseUrl = "http://localhost:11434",
                MaxTokens = 1024,
                Thinking = thinking
            }
        };
        var client = new OllamaClient(http, settings, new FakeWorkspaceService());
        var conversation = new Conversation();
        conversation.AddUser("考えて");

        await foreach (var _ in client.StreamAsync(conversation, Array.Empty<ToolDefinition>(), CancellationToken.None))
        {
        }

        return Assert.Single(handler.PostedBodies);
    }

    private static async Task<List<AgentEvent>> RunLocalAsync(params string[] ndjsonChunks)
    {
        var handler = new SingleResponseHandler(string.Concat(ndjsonChunks));
        using var http = new HttpClient(handler);
        var settings = new AiSettings
        {
            Local = new ProviderConfig { Model = "qwen3:4b", BaseUrl = "http://localhost:11434", MaxTokens = 1024 }
        };
        var client = new OllamaClient(http, settings, new FakeWorkspaceService());
        var conversation = new Conversation();
        conversation.AddUser("こんにちは");

        var events = new List<AgentEvent>();
        await foreach (var ev in client.StreamAsync(conversation, System.Array.Empty<ToolDefinition>(), CancellationToken.None))
            events.Add(ev);
        return events;
    }

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly string _json;
        public SingleResponseHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // GET は /api/tags（起動確認）。POST は /api/chat。
            var content = request.Method == HttpMethod.Get ? "{\"models\":[]}" : _json;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<JsonObject> PostedBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, "{\"models\":[]}");

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            PostedBodies.Add(JsonNode.Parse(body)!.AsObject());

            if (PostedBodies.Count == 1)
            {
                return Json(HttpStatusCode.BadRequest,
                    "{\"error\":\"registry.ollama.ai/library/mistral:7b does not support tools\"}");
            }

            // フォールバック後の成功応答は NDJSON。
            return Json(HttpStatusCode.OK,
                "{\"message\":{\"role\":\"assistant\",\"content\":\"通常チャットで回答します。\"},\"done\":true}\n");
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string content)
            => new(statusCode)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _response;

        public RecordingHandler(string response) => _response = response;

        public List<JsonObject> PostedBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, "{\"models\":[]}");

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            PostedBodies.Add(JsonNode.Parse(body)!.AsObject());
            return Json(HttpStatusCode.OK, _response);
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string content)
            => new(statusCode)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
    }
}
