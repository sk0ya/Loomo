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

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), profile: null, workspaceFolders: System.Array.Empty<string>(), conv, Pwsh());

        Assert.StartsWith("<|system|>", prompt);
        Assert.Contains("<|tool|>[", prompt);
        Assert.Contains("\"name\":\"run_powershell\"", prompt);
        Assert.Contains("\"parameters\":{\"command\":{\"type\":\"string\"", prompt);
        Assert.Contains("<|/tool|><|end|>", prompt);
        Assert.Contains("Tools only: run_powershell, write_file, edit_file, web_search.", prompt);
        Assert.Contains("Examples:", prompt);
        Assert.Contains("<|user|>ファイル一覧を出して<|end|>", prompt);
        Assert.EndsWith("<|assistant|>", prompt);                 // add_generation_prompt
    }

    [Fact]
    public void Omits_tool_guidance_when_no_tools()
    {
        var conv = new Conversation();
        conv.AddUser("やあ");

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), null, System.Array.Empty<string>(), conv, System.Array.Empty<ToolDefinition>());

        Assert.DoesNotContain("<|tool|>", prompt);
        Assert.Contains("<|system|>", prompt);
        Assert.EndsWith("<|assistant|>", prompt);
    }

    [Fact]
    public void Includes_current_folder_in_system_prompt()
    {
        var conv = new Conversation();
        conv.AddUser("これは何？");

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), null, new[] { "C:\\proj" }, conv, System.Array.Empty<ToolDefinition>());

        // 現在のフォルダは準安定値なので system（安定プレフィックス）に載せる。
        Assert.Contains("現在のフォルダ", prompt);
        Assert.Contains("C:\\proj", prompt);
    }

    [Fact]
    public void Includes_all_workspace_folders_when_multi_root()
    {
        var conv = new Conversation();
        conv.AddUser("これは何？");

        var prompt = Phi4PromptFormatter.Build(
            new AiSettings(), null, new[] { "C:\\proj", "C:\\other" }, conv, System.Array.Empty<ToolDefinition>());

        Assert.Contains("C:\\proj", prompt);
        Assert.Contains("C:\\other", prompt);
        // 相対パス解決の基準（プライマリ）はワークスペースルート1件のみなので、
        // 追加フォルダーは絶対パスで参照するようモデルへ明示する。
        Assert.Contains("絶対パス", prompt);
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

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), null, System.Array.Empty<string>(), conv, Pwsh());

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

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), null, System.Array.Empty<string>(), conv, Pwsh());

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
        var root = new[] { "C:\\proj" };

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
    public void Render_prefix_is_injected_before_user_text_in_the_user_turn()
    {
        var conv = new Conversation();
        var user = conv.AddUser("次の文章を英語に翻訳してください。\n\n対象:\nこんにちは");
        user.RenderPrefix = AiSettings.WorkflowTurnPreamble;

        var prompt = Phi4PromptFormatter.Build(new AiSettings(), null, System.Array.Empty<string>(), conv, Pwsh());

        // 追加プロンプトはユーザーターン内（<|user|>…<|end|>）で、本文の前に入る。
        Assert.Contains("<|user|>" + AiSettings.WorkflowTurnPreamble + "\n\n次の文章を英語に翻訳してください。", prompt);
    }

    [Fact]
    public void Render_prefix_lives_after_the_warmup_prefix_so_kv_sharing_holds()
    {
        // モード別の追加プロンプトを user ターンへ入れても、暖機する system ブロック（=最長共通接頭辞）は不変。
        var settings = new AiSettings();
        var tools = Pwsh();
        var root = new[] { "C:\\proj" };

        var warmup = Phi4PromptFormatter.Build(settings, AgentProfiles.Root, root, new Conversation(), tools);
        var systemBlock = warmup[..^"<|assistant|>".Length];

        var conv = new Conversation();
        var user = conv.AddUser("ファイル一覧を出して");
        user.RenderPrefix = AiSettings.ChatTurnPreamble;
        var real = Phi4PromptFormatter.Build(settings, AgentProfiles.Root, root, conv, tools);

        // 追加文があっても system ブロックの直後（user ターン）でだけ差分が出る。
        Assert.StartsWith(systemBlock + "<|user|>" + AiSettings.ChatTurnPreamble, real);
    }

    [Fact]
    public void Default_system_prompt_is_engine_neutral_and_guides_tool_calling()
    {
        Assert.DoesNotContain("Ollama", AiSettings.DefaultSystemPrompt);
        Assert.Contains("Tools only: run_powershell, write_file, edit_file, web_search.", AiSettings.DefaultSystemPrompt);
        Assert.Contains("optionally wrapped in <|tool_call|>", AiSettings.DefaultSystemPrompt);
        // run_powershell の呼び出し形を具体例で示し、小モデルが rg/read_file/build 等の架空ツール名へ崩れるのを抑える。
        Assert.Contains("\"name\":\"run_powershell\"", AiSettings.DefaultSystemPrompt);
        Assert.Contains("\"arguments\":{\"command\":\"Get-ChildItem\"}", AiSettings.DefaultSystemPrompt);
        Assert.Contains("Read file", AiSettings.DefaultSystemPrompt);
        Assert.Contains("Search code", AiSettings.DefaultSystemPrompt);
        Assert.Contains("dotnet build", AiSettings.DefaultSystemPrompt);
        Assert.Contains("read_file/search/build are commands or files, not tools", AiSettings.DefaultSystemPrompt);
        Assert.Contains("Use tools for workspace facts/actions", AiSettings.DefaultSystemPrompt);
        Assert.Contains("answer chat directly", AiSettings.DefaultSystemPrompt);
        Assert.Contains("non-interactive", AiSettings.DefaultSystemPrompt);
        // 構造化ファイルツールがプロンプトに告知されていること。
        Assert.Contains("write_file", AiSettings.DefaultSystemPrompt);
        Assert.Contains("\"name\":\"edit_file\"", AiSettings.DefaultSystemPrompt);
        Assert.Contains("\"name\":\"web_search\"", AiSettings.DefaultSystemPrompt);
        Assert.Contains("Read before editing existing files", AiSettings.DefaultSystemPrompt);
        Assert.Contains("no Set-Content", AiSettings.DefaultSystemPrompt);
    }
}
