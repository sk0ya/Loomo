using System.Linq;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Tests;

/// <summary>Phi-4 チャットテンプレート組み立て（<see cref="Phi4PromptFormatter"/>）のテスト。</summary>
public class Phi4PromptFormatterTests
{
    private static ToolDefinition[] Pwsh() =>
        new[] { new ToolDefinition("run_powershell", "run", ToolDefinition.ObjectSchema(
            ("command", "string", "PowerShell command", true))) };

    [Fact]
    public void Builds_system_with_tool_block_and_opens_assistant()
    {
        var conv = new Conversation();
        conv.AddUser("ファイル一覧を出して");

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), profile: null, workspaceRoot: null, conv, Pwsh());

        Assert.StartsWith("<|system|>", prompt);
        Assert.Contains("<|tool|>[", prompt);
        Assert.Contains("\"name\":\"run_powershell\"", prompt);
        Assert.Contains("\"parameters\":{\"command\":{\"type\":\"string\"", prompt);
        Assert.Contains("<|/tool|><|end|>", prompt);
        Assert.Contains("output exactly [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"<PowerShell command>\"}}]", prompt);
        Assert.Contains("The first character must be [", prompt);
        Assert.Contains("<|user|>ファイル一覧を出して<|end|>", prompt);
        Assert.EndsWith("<|assistant|>", prompt);                 // add_generation_prompt
    }

    [Fact]
    public void Omits_tool_guidance_when_no_tools()
    {
        var conv = new Conversation();
        conv.AddUser("やあ");

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), null, null, conv, System.Array.Empty<ToolDefinition>());

        Assert.DoesNotContain("<|tool|>", prompt);
        Assert.Contains("<|system|>", prompt);
        Assert.EndsWith("<|assistant|>", prompt);
    }

    [Fact]
    public void Includes_current_folder_in_system_prompt()
    {
        var conv = new Conversation();
        conv.AddUser("これは何？");

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), null, "C:\\proj", conv, System.Array.Empty<ToolDefinition>());

        // 現在のフォルダは準安定値なので system（安定プレフィックス）に載せる。
        Assert.Contains("現在のフォルダ", prompt);
        Assert.Contains("C:\\proj", prompt);
    }

    [Fact]
    public void Renders_assistant_tool_calls_and_tool_results_as_turns()
    {
        var conv = new Conversation();
        conv.AddUser("ビルドして");
        var assistant = new ChatMessage { Role = ChatRole.Assistant, Text = "" };
        assistant.ToolUses.Add(new ToolUse("t1", "run_powershell", "{\"command\":\"dotnet build\"}"));
        conv.Messages.Add(assistant);
        var tool = new ChatMessage { Role = ChatRole.Tool };
        tool.ToolResults.Add(new ToolResultMessage("t1", "成功", IsError: false));
        conv.Messages.Add(tool);

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), null, null, conv, Pwsh());

        // アシスタントの過去ツール呼び出しはモデル出力と同じ JSON 配列形式で履歴に残す。
        Assert.Contains("<|assistant|>[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"dotnet build\"}}]<|end|>", prompt);
        // ツール結果は role "tool"（<|tool|>content<|end|>）として描画される。
        Assert.Contains("<|tool|>成功<|end|>", prompt);
    }

    [Fact]
    public void Default_system_prompt_is_engine_neutral_and_guides_tool_calling()
    {
        Assert.DoesNotContain("Ollama", AiSettings.DefaultSystemPrompt);
        Assert.Contains("tool-calling loop", AiSettings.DefaultSystemPrompt);
        Assert.Contains("output exactly [{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"<PowerShell command>\"}}]", AiSettings.DefaultSystemPrompt);
        Assert.Contains("The first character must be [", AiSettings.DefaultSystemPrompt);
    }
}
