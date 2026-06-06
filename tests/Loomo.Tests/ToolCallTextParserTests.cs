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
    public void Converts_tool_call_json_array_multiple_entries()
    {
        var tools = ToolCallTextParser.Parse(
            "[{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"a\"}}," +
            "{\"name\":\"run_powershell\",\"arguments\":{\"command\":\"b\"}}]").ToList();
        Assert.Equal(2, tools.Count);
        Assert.Equal("{\"command\":\"a\"}", tools[0].ArgumentsJson);
        Assert.Equal("{\"command\":\"b\"}", tools[1].ArgumentsJson);
    }
}
