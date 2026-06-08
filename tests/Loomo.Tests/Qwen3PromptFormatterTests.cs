using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Tests;

/// <summary>Qwen3 ChatML テンプレート組み立て（<see cref="Qwen3PromptFormatter"/>）のテスト。</summary>
public class Qwen3PromptFormatterTests
{
    private static ToolDefinition[] Pwsh() =>
        new[] { new ToolDefinition("run_powershell", "run", ToolDefinition.ObjectSchema(
            ("command", "string", "PowerShell command", true))) };

    [Fact]
    public void Builds_chatml_system_with_hermes_tools_block()
    {
        var conv = new Conversation();
        conv.AddUser("ファイル一覧を出して");

        var prompt = Qwen3PromptFormatter.Build(new AiSettings(), profile: null, workspaceRoot: null, conv, Pwsh());

        Assert.StartsWith("<|im_start|>system\n", prompt);
        Assert.Contains("<tools>\n", prompt);
        Assert.Contains("\"type\":\"function\"", prompt);
        Assert.Contains("\"function\":{\"name\":\"run_powershell\"", prompt);
        Assert.Contains("</tools>", prompt);
        Assert.Contains("<tool_call></tool_call>", prompt);
        Assert.Contains("<|im_start|>user\nファイル一覧を出して<|im_end|>\n", prompt);
        // 生成開始マーカ＋thinking 無効化（空 think プレフィル）で終わる。
        Assert.EndsWith("<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt);
        // Qwen3 用システムプロンプト（Hermes 記法の例文）が入っている。
        Assert.Contains("<tool_call>{\"name\":\"run_powershell\"", prompt);
        // phi4 専用のパイプ記法は使わない。
        Assert.DoesNotContain("<|tool_call|>", prompt);
    }

    [Fact]
    public void Omits_tools_block_when_no_tools()
    {
        var conv = new Conversation();
        conv.AddUser("やあ");

        var prompt = Qwen3PromptFormatter.Build(new AiSettings(), null, null, conv, System.Array.Empty<ToolDefinition>());

        Assert.DoesNotContain("<tools>", prompt);
        Assert.Contains("<|im_start|>system\n", prompt);
    }

    [Fact]
    public void Renders_assistant_tool_calls_and_tool_results_as_chatml_turns()
    {
        var conv = new Conversation();
        conv.AddUser("ビルドして");
        var assistant = new ChatMessage { Role = ChatRole.Assistant, Text = "" };
        assistant.ToolUses.Add(new ToolUse("t1", "run_powershell", "{\"command\":\"dotnet build\"}"));
        conv.Messages.Add(assistant);
        var tool = new ChatMessage { Role = ChatRole.Tool };
        tool.ToolResults.Add(new ToolResultMessage("t1", "成功", IsError: false));
        conv.Messages.Add(tool);

        var prompt = Qwen3PromptFormatter.Build(new AiSettings(), null, null, conv, Pwsh());

        Assert.Contains(
            "<|im_start|>assistant\n<tool_call>\n{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"dotnet build\"}}\n</tool_call><|im_end|>\n",
            prompt);
        Assert.Contains("<|im_start|>user\n<tool_response>\n成功\n</tool_response><|im_end|>\n", prompt);
    }

    [Fact]
    public void Qwen3_profile_selects_chatml_format_via_dispatcher()
    {
        var profile = ModelProfiles.Resolve("qwen3-1.7b-cpu-int4");
        Assert.Equal(ChatFormat.Qwen3, profile.Format);

        var conv = new Conversation();
        conv.AddUser("やあ");
        var prompt = ChatPrompt.Build(profile.Format, new AiSettings(), null, null, conv, Pwsh());
        Assert.StartsWith("<|im_start|>system\n", prompt);
    }

    [Fact]
    public void Resolve_maps_qwen3_folder_names()
    {
        Assert.Equal("qwen3", ModelProfiles.Resolve("qwen3-1.7b-cpu-int4").Family);
        Assert.Equal("qwen3", ModelProfiles.Resolve("qwen3-4b-cpu-int4").Family);
        Assert.Equal("phi4-mini", ModelProfiles.Resolve("phi-4-mini-instruct-cpu-int4").Family);
    }
}
