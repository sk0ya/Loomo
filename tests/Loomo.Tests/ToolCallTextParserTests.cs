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
        // 実際に観測された出力：先頭の run_powershell は正しいが、続く write_file 群が
        // "content="（: 抜け・クオート欠落）で不正。配列全体は JsonNode.Parse 不能。
        // 先頭の1オブジェクトだけ救って実行できること（巻き添えで全捨てしない）。
        const string malformed =
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"Get-ChildItem -Recurse -Filter *.html | Where-Object { $_.Length -gt 0 }\"}}, " +
            "{\"name\":\"write_file\",\"arguments\":{\"path\":\"C:\\\\t\\\\a.html\",\"content=\"<html></html>\"}}]";

        var tool = Assert.Single(ToolCallTextParser.Parse(malformed));
        Assert.Equal("run_powershell", tool.Name);
        using var args = JsonDocument.Parse(tool.ArgumentsJson);
        Assert.Contains("Get-ChildItem", args.RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void Returns_empty_when_first_object_itself_is_malformed()
    {
        // 先頭が壊れていれば救えない（空を返す）。クライアント側が不正JSONとして仕切り直させる。
        Assert.Empty(ToolCallTextParser.Parse("[{\"name\":\"write_file\",\"content=\"oops\"}]"));
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
}
