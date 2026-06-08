using System.Linq;
using System.Text.Json;
using sk0ya.Loomo.Ai.Clients;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 本文テキスト → ツール呼び出し変換（<see cref="ToolCallTextParser"/>）のテスト。
/// 小型ローカルLLM（Ollama・Phi-4 いずれも）が構造化 tool_calls ではなく本文に書いた呼び出しを拾う。
/// </summary>
public class ToolCallTextParserTests
{
    [Fact]
    public void Keeps_plain_text_without_tool_call_as_empty()
    {
        Assert.Empty(ToolCallTextParser.Parse("run_powershell {Get-Location}"));
        Assert.Empty(ToolCallTextParser.Parse("こんにちは、これは普通の文章です。"));
    }

    [Fact]
    public void Converts_function_call_style()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse("run_powershell(\"Get-Location\")"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"Get-Location\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_function_call_with_windows_path_and_scriptblock()
    {
        const string command = "Get-Item -Path 'C:\\SDD\\SDD_Projects\\NEX_Doc' -Recurse | Where-Object { $_.PSIsContainer }";
        var tool = Assert.Single(ToolCallTextParser.Parse($"run_powershell(\"{command}\")"));
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Equal(command, args.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Converts_empty_function_call()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse("run_powershell(\"\")"));
        Assert.Equal("{\"command\":\"\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_bare_arguments_json_object()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse("{\"command\":\"Get-ChildItem\"}"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"Get-ChildItem\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_run_powershell_prefixed_json_arguments()
    {
        const string command = "git log --diff-filter=M --name-status --pretty='%B'";
        var tool = Assert.Single(ToolCallTextParser.Parse($"run_powershell {{\"command\":\"{command}\"}}"));
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Equal(command, args.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Converts_alias_key_with_code_fence_and_name_arguments_wrap()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "```json\n{\"name\":\"run_powershell\",\"arguments\":{\"cmd\":\"ls\"}}\n```"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"ls\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_tool_call_json_array_single_entry()
    {
        // Phi-4-mini はツール呼び出しをこの JSON 配列形式で本文に返す。
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}]"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"Get-ChildItem\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_phi4_tool_call_wrapped_json_array()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "<|tool_call|>[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}]<|/tool_call|>"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"Get-ChildItem\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_phi4_tool_response_wrapped_json_array_when_model_uses_wrong_tag()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "<|tool_response|>[{\"name\":\"write_file\",\"arguments\":{\"path\":\"a.txt\",\"content\":\"A\"}}]"));
        Assert.Equal("write_file", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Equal("a.txt", args.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public void Converts_tool_call_json_array_embedded_in_narration()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "確認します。\n[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}]\n実行します。"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"Get-ChildItem\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_tool_call_json_object_embedded_in_narration()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "まずこれを使います: {\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-Location\"}}"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"Get-Location\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_parameters_object_when_model_confuses_schema_with_arguments()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "[{\"name\":\"run_powershell\",\"description\":\"Obtain the current working directory by returning the shell's printed path and exit code.\",\"parameters\":{\"command\":\"Get-Location\"}}]"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"Get-Location\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_tool_call_array_missing_opening_bracket()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "{\"name\":\"run_powershell\",\"description\":\"Obtain the current working directory by returning stdout and exit code.\",\"parameters\":{\"command\":\"Get-Location\"}}]"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"Get-Location\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_arguments_json_missing_opening_brace()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse("\"command\":\"Get-Location\"}"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"Get-Location\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_tool_call_json_array_multiple_entries()
    {
        var tools = ToolCallTextParser.Parse(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"a\"}}," +
            "{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"b\"}}]").ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal("{\"command\":\"a\"}", tools[0].ArgumentsJson);
        Assert.Equal("{\"command\":\"b\"}", tools[1].ArgumentsJson);
    }

    [Fact]
    public void Routes_write_file_tool_call_with_arguments_passed_through()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "[{\"name\":\"write_file\",\"arguments\":{\"path\":\"a.txt\",\"content\":\"hi\"}}]"));
        Assert.Equal("write_file", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Equal("a.txt", args.RootElement.GetProperty("path").GetString());
        Assert.Equal("hi", args.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Routes_edit_file_tool_call_with_flat_arguments()
    {
        // 引数を arguments ラップ無しでフラットに吐くこともある。name 以外を引数とみなす。
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "{\"name\":\"edit_file\",\"path\":\"a.cs\",\"old_string\":\"foo\",\"new_string\":\"bar\"}"));
        Assert.Equal("edit_file", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Equal("foo", args.RootElement.GetProperty("old_string").GetString());
        Assert.Equal("bar", args.RootElement.GetProperty("new_string").GetString());
        Assert.False(args.RootElement.TryGetProperty("name", out _));
    }

    [Fact]
    public void Salvages_first_valid_object_when_later_entries_are_malformed_json()
    {
        // 先頭の run_powershell は正しいが、続く write_file はカンマ欠落で構造ごと不正（補修でも直らない）。
        // 配列全体は JsonNode.Parse 不能。先頭の1オブジェクトだけ救って実行できること（巻き添えで全捨てしない）。
        const string malformed =
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem -Recurse -Filter *.html | Where-Object { $_.Length -gt 0 }\"}}, " +
            "{\"name\":\"write_file\" \"arguments\":{\"path\":\"a.html\"}}]";

        var tool = Assert.Single(ToolCallTextParser.Parse(malformed));
        Assert.Equal("run_powershell", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Contains("Get-ChildItem", args.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Returns_empty_when_first_object_itself_is_malformed()
    {
        // 先頭が（補修しても）壊れていれば救えない（空を返す）。クライアント側が不正JSONとして仕切り直させる。
        Assert.Empty(ToolCallTextParser.Parse("[{\"name\":\"write_file\",\"content\":}]"));
    }

    [Fact]
    public void Repairs_key_assignment_typo_colon_written_as_equals()
    {
        // 実測: create-nested タスクでモデルが "content":" を "content=" と書き、配列全体がパース不能に。
        // 既知キーの "key=" は ":" の打ち間違いと一意に判定できるので補修して救済する。
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "[{\"name\":\"write_file\",\"arguments\":{\"path\":\"docs/guide/intro.md\",\"content=\"# Intro\\nWelcome.\"}}]"));
        Assert.Equal("write_file", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Equal("docs/guide/intro.md", args.RootElement.GetProperty("path").GetString());
        Assert.Equal("# Intro\nWelcome.", args.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Repairs_invalid_regex_backslash_escape_in_command()
    {
        // 実測: edit-file タスクでモデルが PowerShell 正規表現 '1\.2\.3' を JSON 文字列に素で書き、
        // \. が無効エスケープになって配列全体がパース不能に。バックスラッシュ二重化で救済する。
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-Content README.md | Select-String '1\\.2\\.3'\"}}]"));
        Assert.Equal("run_powershell", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        // 復元後のコマンド文字列にはモデルが意図した正規表現 \.（リテラルのドット）がそのまま残る。
        Assert.Contains("'1\\.2\\.3'", args.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Repairs_invalid_backslash_path_escape()
    {
        // 実測: multi-step タスクで .\src\util.txt と書き、\s/\u(非hex) が無効エスケープに。
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-Content .\\src\\util.txt\"}}]"));
        Assert.Equal("run_powershell", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Equal("Get-Content .\\src\\util.txt", args.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Keeps_valid_escapes_intact_while_repairing()
    {
        // 有効な \n と無効な \d が混在。\n は保持、\d だけ二重化されること。
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "[{\"name\":\"write_file\",\"arguments\":{\"path\":\"a.txt\",\"content\":\"line1\\nmatch \\d+ digits\"}}]"));
        Assert.Equal("write_file", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Equal("line1\nmatch \\d+ digits", args.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Routes_mixed_tools_in_one_array()
    {
        var tools = ToolCallTextParser.Parse(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}," +
            "{\"name\":\"write_file\",\"arguments\":{\"path\":\"b.txt\",\"content\":\"x\"}}]").ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal("run_powershell", tools[0].Name);
        Assert.Equal("{\"command\":\"ls\"}", tools[0].ArgumentsJson);
        Assert.Equal("write_file", tools[1].Name);
    }

    [Fact]
    public void Converts_qwen3_hermes_tool_call_tag()
    {
        // Qwen3 は Hermes 形式 <tool_call>{…}</tool_call> でツールを呼ぶ。
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "<tool_call>\n{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem\"}}\n</tool_call>"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"Get-ChildItem\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Converts_multiple_qwen3_hermes_tool_calls()
    {
        var tools = ToolCallTextParser.Parse(
            "<tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"a\"}}</tool_call>\n" +
            "<tool_call>{\"name\":\"write_file\",\"arguments\":{\"path\":\"b.txt\",\"content\":\"x\"}}</tool_call>").ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal("{\"command\":\"a\"}", tools[0].ArgumentsJson);
        Assert.Equal("write_file", tools[1].Name);
    }

    [Fact]
    public void Strips_think_block_before_qwen3_tool_call()
    {
        // no_think でも空の <think></think> が前置されることがある。除去してから tool call を拾う。
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "<think>\n\n</think>\n\n<tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}</tool_call>"));
        Assert.Equal("run_powershell", tool.Name);
        Assert.Equal("{\"command\":\"ls\"}", tool.ArgumentsJson);
    }

    [Fact]
    public void Recovers_qwen3_tool_call_missing_closing_tag()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "<tool_call>{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"ls\"}}"));
        Assert.Equal("run_powershell", tool.Name);
    }

    [Fact]
    public void StripThinkBlocks_removes_think_keeps_answer()
    {
        Assert.Equal("最終回答です。",
            ToolCallTextParser.StripThinkBlocks("<think>考え中…</think>\n最終回答です。"));
        Assert.Equal("普通の回答", ToolCallTextParser.StripThinkBlocks("普通の回答"));
    }

    [Fact]
    public void Repairs_key_colon_written_as_comma_in_qwen3_tool_call()
    {
        // 実測(Qwen3-1.7B create-nested): キー直後を ":" でなく "," と書いて配列がパース不能に。
        // 既知キーの "content"," は一意にこの誤りなので "content":" へ補修して救済する。
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "<tool_call>{\"name\":\"write_file\",\"arguments\":{\"path\":\"docs/guide/intro.md\",\"content\",\"# Intro\\n\"}}</tool_call>"));
        Assert.Equal("write_file", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Equal("docs/guide/intro.md", args.RootElement.GetProperty("path").GetString());
        Assert.Equal("# Intro\n", args.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Repairs_old_string_colon_written_as_comma()
    {
        var tool = Assert.Single(ToolCallTextParser.Parse(
            "[{\"name\":\"edit_file\",\"arguments\":{\"path\":\"a.md\",\"old_string\",\"foo\",\"new_string\":\"bar\"}}]"));
        Assert.Equal("edit_file", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Equal("foo", args.RootElement.GetProperty("old_string").GetString());
        Assert.Equal("bar", args.RootElement.GetProperty("new_string").GetString());
    }
}
