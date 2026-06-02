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

            return Json(HttpStatusCode.OK,
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"通常チャットで回答します。\"}}]}");
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string content)
            => new(statusCode)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
    }
}
