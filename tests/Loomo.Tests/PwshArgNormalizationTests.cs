using System.Text.Json;
using System.Text.Json.Nodes;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools.Implementations;

namespace sk0ya.Loomo.Tests;

/// <summary>pwsh の引数正規化：小モデルのキー揺れ（cmd/script 等）と空引数の扱い。
/// NormalizeArguments は _terminal を使わない純粋変換なので null で構築してよい。</summary>
public class PwshArgNormalizationTests
{
    private static readonly PwshTool Tool = new(terminal: null!);

    private static string CommandOf(string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var normalized = Tool.NormalizeArguments(doc.RootElement);
        return normalized.GetProperty("command").GetString() ?? "";
    }

    [Theory]
    [InlineData("{\"command\":\"Get-ChildItem\"}")]
    [InlineData("{\"cmd\":\"Get-ChildItem\"}")]
    [InlineData("{\"script\":\"Get-ChildItem\"}")]
    [InlineData("{\"code\":\"Get-ChildItem\"}")]
    [InlineData("{\"powershell\":\"Get-ChildItem\"}")]
    public void Aliases_are_normalized_to_command(string argsJson)
        => Assert.Equal("Get-ChildItem", CommandOf(argsJson));

    [Fact]
    public void Canonical_command_wins_over_alias()
        => Assert.Equal("real", CommandOf("{\"command\":\"real\",\"cmd\":\"ignored\"}"));

    [Fact]
    public void Single_unknown_string_property_is_adopted_as_command()
        => Assert.Equal("Get-Location", CommandOf("{\"line\":\"Get-Location\"}"));

    [Fact]
    public void Lone_arbitrary_string_property_is_adopted()
        => Assert.Equal("whoami", CommandOf("{\"foo\":\"whoami\"}"));

    [Fact]
    public void Ambiguous_multiple_strings_without_known_key_yield_empty()
        => Assert.Equal("", CommandOf("{\"foo\":\"a\",\"bar\":\"b\"}"));

    [Fact]
    public void Empty_object_yields_empty_command()
        => Assert.Equal("", CommandOf("{}"));

    [Fact]
    public void Definition_uses_short_english_schema_text_for_small_models()
    {
        var definition = Tool.Definition;
        Assert.Equal("Run one non-interactive PowerShell command and return stdout and exit code.", definition.Description);
        Assert.Equal(
            "Non-empty non-interactive command; avoid pagers and prompts.",
            definition.InputSchema["properties"]!["command"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Execute_inserts_no_pager_for_git_commands()
    {
        var terminal = new CapturingTerminal();
        var tool = new PwshTool(terminal);
        using var doc = JsonDocument.Parse("{\"command\":\"git diff -- src\"}");

        await tool.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.Equal("git --no-pager diff -- src", terminal.LastCommand);
    }

    [Fact]
    public async Task Execute_keeps_existing_no_pager_git_commands()
    {
        var terminal = new CapturingTerminal();
        var tool = new PwshTool(terminal);
        using var doc = JsonDocument.Parse("{\"command\":\"git --no-pager status --short\"}");

        await tool.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.Equal("git --no-pager status --short", terminal.LastCommand);
    }

    private sealed class CapturingTerminal : ITerminalService
    {
        public string? LastCommand { get; private set; }
        public string CurrentDirectory => "C:\\Projects\\Loomo";
        public bool IsExecuting => false;

        public Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
        {
            LastCommand = command;
            return Task.FromResult(new CommandResult(command, "", 0, CurrentDirectory, true));
        }

        public void SetWorkingDirectory(string path) { }
        public bool TryRunInVisibleTerminal(string command) => false;

#pragma warning disable CS0067
        public event EventHandler<CommandResult>? CommandExecuted;
#pragma warning restore CS0067
    }
}
