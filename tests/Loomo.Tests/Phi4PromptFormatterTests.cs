using System.Linq;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Agent;
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
        Assert.Contains("Output exactly [{\"name\":\"<tool>\",\"arguments\":{...}}, ...]", prompt);
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
    public void Renders_assistant_text_after_tool_calls_to_preserve_stream_order()
    {
        var conv = new Conversation();
        conv.AddUser("一覧を出して");
        var assistant = new ChatMessage { Role = ChatRole.Assistant, Text = "一覧を取得します。" };
        assistant.ToolUses.Add(new ToolUse("t1", "run_powershell", "{\"command\":\"ls\"}"));
        conv.Messages.Add(assistant);

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), null, null, conv, Pwsh());

        Assert.Contains(
            "<|assistant|>[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}]\n一覧を取得します。<|end|>",
            prompt);
    }

    [Fact]
    public void Warmup_prefix_is_an_exact_prefix_of_the_first_real_turn()
    {
        // 暖機（LocalLlmWarmupService）は会話を空にして Build した文字列で常駐 Generator を温める。
        // その system ブロックが、実ターン（同じ settings/profile/root/tools ＋<|user|>…）の
        // 先頭と完全一致することが、KV プレフィックス再利用の前提。ここを回帰として固定する。
        var settings = new AiSettings();
        var tools = Pwsh();
        const string root = "C:\\proj";

        var warmup = Phi4PromptFormatter.Build(settings, AgentProfiles.Root, root, new Conversation(), tools);

        var conv = new Conversation();
        conv.AddUser("ファイル一覧を出して");
        var real = Phi4PromptFormatter.Build(settings, AgentProfiles.Root, root, conv, tools);

        // 暖機文字列は末尾の <|assistant|>（生成開始マーカ）を除けば実ターンの接頭辞になっている。
        Assert.EndsWith("<|assistant|>", warmup);
        var systemBlock = warmup[..^"<|assistant|>".Length];
        Assert.StartsWith(systemBlock, real);
        // system ブロックの直後で初めて分岐する（暖機は<|assistant|>、実ターンは<|user|>）。
        Assert.StartsWith(systemBlock + "<|user|>", real);
    }

    [Fact]
    public void Default_system_prompt_is_engine_neutral_and_guides_tool_calling()
    {
        Assert.DoesNotContain("Ollama", AiSettings.DefaultSystemPrompt);
        Assert.Contains("tool-calling loop", AiSettings.DefaultSystemPrompt);
        Assert.Contains("Output exactly [{\"name\":\"<tool>\",\"arguments\":{...}}, ...]", AiSettings.DefaultSystemPrompt);
        Assert.Contains("The first character must be [", AiSettings.DefaultSystemPrompt);
        // 独立した操作のみ複数許可（依存する操作は1つずつ）という方針が告知されていること。
        Assert.Contains("INDEPENDENT operations", AiSettings.DefaultSystemPrompt);
        Assert.Contains("MUST be separate steps", AiSettings.DefaultSystemPrompt);
        // 追加した構造化ファイルツールがプロンプトに告知されていること。
        Assert.Contains("write_file{path,content}", AiSettings.DefaultSystemPrompt);
        Assert.Contains("edit_file{path,old_string,new_string}", AiSettings.DefaultSystemPrompt);
    }
}
