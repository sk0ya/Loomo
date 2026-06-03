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

public class OpenAiCompatibleClientTests
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

        var body = OpenAiProtocol.BuildRequest(
            conversation,
            new[] { new ToolDefinition("run_command", "run", ToolDefinition.ObjectSchema()) },
            "gemma3:4b",
            1024,
            "system",
            includeTools: false);

        Assert.False(body.ContainsKey("tools"));
        Assert.False(body.ContainsKey("tool_choice"));

        var messages = body["messages"]!.AsArray();
        Assert.DoesNotContain(messages, m => m?["role"]?.GetValue<string>() == "tool");
        Assert.DoesNotContain(messages, m => m?["tool_calls"] is not null);
        Assert.Contains(messages, m => m?["content"]?.GetValue<string>().Contains("過去のツール実行結果") == true);
    }

    [Fact]
    public async Task Local_client_retries_without_tools_when_ollama_model_rejects_tools()
    {
        var handler = new ScriptedHandler();
        using var http = new HttpClient(handler);
        var settings = new AiSettings
        {
            Local = new ProviderConfig
            {
                Model = "gemma3:4b",
                BaseUrl = "http://localhost:11434/v1",
                MaxTokens = 1024
            }
        };
        var client = new OpenAiCompatibleClient(http, settings, AiProvider.Local);
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
    public async Task Local_client_splits_think_tags_into_thinking_and_text()
    {
        // 思考タグがチャンクを跨いで届いても復元できることも併せて検証する。
        var events = await RunLocalAsync(
            Sse("{\"choices\":[{\"delta\":{\"content\":\"<thi\"}}]}"),
            Sse("{\"choices\":[{\"delta\":{\"content\":\"nk>まず手順を\"}}]}"),
            Sse("{\"choices\":[{\"delta\":{\"content\":\"整理する。</think>こんにちは！\"}}]}"),
            "data: [DONE]\n\n");

        var thinking = string.Concat(events.OfType<ThinkingDelta>().Select(t => t.Text));
        var text = string.Concat(events.OfType<TextDelta>().Select(t => t.Text));
        Assert.Equal("まず手順を整理する。", thinking);
        Assert.Equal("こんにちは！", text);
    }

    [Fact]
    public async Task Local_client_surfaces_reasoning_content_field_as_thinking()
    {
        var events = await RunLocalAsync(
            Sse("{\"choices\":[{\"delta\":{\"reasoning_content\":\"ユーザーは挨拶している。\"}}]}"),
            Sse("{\"choices\":[{\"delta\":{\"content\":\"やあ\"}}]}"),
            "data: [DONE]\n\n");

        Assert.Contains(events, e => e is ThinkingDelta { Text: "ユーザーは挨拶している。" });
        Assert.Contains(events, e => e is TextDelta { Text: "やあ" });
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("thinking")]
    public async Task Local_client_surfaces_ollama_reasoning_fields_as_thinking(string fieldName)
    {
        var events = await RunLocalAsync(
            Sse($"{{\"choices\":[{{\"delta\":{{\"{fieldName}\":\"計算している。\"}}}}]}}"),
            Sse("{\"choices\":[{\"delta\":{\"content\":\"答えは2です。\"}}]}"),
            "data: [DONE]\n\n");

        Assert.Contains(events, e => e is ThinkingDelta { Text: "計算している。" });
        Assert.Contains(events, e => e is TextDelta { Text: "答えは2です。" });
    }

    [Fact]
    public async Task Local_client_treats_unclosed_think_as_thinking()
    {
        var events = await RunLocalAsync(
            Sse("{\"choices\":[{\"delta\":{\"content\":\"<think>まだ考えている途中\"}}]}"),
            "data: [DONE]\n\n");

        var thinking = string.Concat(events.OfType<ThinkingDelta>().Select(t => t.Text));
        Assert.Equal("まだ考えている途中", thinking);
        Assert.DoesNotContain(events, e => e is TextDelta);
    }

    [Fact]
    public async Task Local_client_assembles_streamed_tool_call_fragments()
    {
        var events = await RunLocalAsync(
            Sse("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"run_command\",\"arguments\":\"\"}}]}}]}"),
            Sse("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"command\\\":\"}}]}}]}"),
            Sse("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"ls\\\"}\"}}]}}]}"),
            "data: [DONE]\n\n");

        var tool = Assert.Single(events.OfType<ToolUseRequested>());
        Assert.Equal("c1", tool.ToolUse.Id);
        Assert.Equal("run_command", tool.ToolUse.Name);
        Assert.Equal("{\"command\":\"ls\"}", tool.ToolUse.ArgumentsJson);
        Assert.DoesNotContain(events, e => e is TurnCompleted); // ツール継続のため出さない
    }

    private static string Sse(string jsonLine) => $"data: {jsonLine}\n\n";

    private static async Task<List<AgentEvent>> RunLocalAsync(params string[] sseChunks)
    {
        var handler = new SingleResponseHandler(string.Concat(sseChunks));
        using var http = new HttpClient(handler);
        var settings = new AiSettings
        {
            Local = new ProviderConfig { Model = "r1", BaseUrl = "http://localhost:11434/v1", MaxTokens = 1024 }
        };
        var client = new OpenAiCompatibleClient(http, settings, AiProvider.Local);
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
            var content = request.Method == HttpMethod.Get ? "{\"data\":[]}" : _json;
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
                return Json(HttpStatusCode.OK, "{\"data\":[]}");

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            PostedBodies.Add(JsonNode.Parse(body)!.AsObject());

            if (PostedBodies.Count == 1)
            {
                return Json(HttpStatusCode.BadRequest,
                    "{\"error\":{\"message\":\"registry.ollama.ai/library/gemma3:4b does not support tools\"}}");
            }

            // フォールバック後の成功応答は SSE（ローカルはストリーミング）。
            return Json(HttpStatusCode.OK,
                "data: {\"choices\":[{\"delta\":{\"content\":\"通常チャットで回答します。\"}}]}\n\n" +
                "data: [DONE]\n\n");
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string content)
            => new(statusCode)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
    }
}
