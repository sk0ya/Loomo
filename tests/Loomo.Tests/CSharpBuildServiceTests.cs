using sk0ya.Loomo.CSharp.Build;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Tests;

/// <summary>CSharp DLLに移したdotnet buildコマンドの生成・実行を検証する。</summary>
public sealed class CSharpBuildServiceTests
{
    [Fact]
    public void BuildCommand_contains_target_configuration_and_no_logo()
    {
        var command = CSharpBuildService.BuildCommand(
            @"C:\work space\Sample.sln", "Release");

        Assert.Equal(
            "dotnet build 'C:\\work space\\Sample.sln' -c 'Release' --nologo",
            command);
    }

    [Fact]
    public void BuildCommand_can_target_a_specific_framework()
    {
        var command = CSharpBuildService.BuildCommand(
            @"C:\work\Sample.csproj", "Debug", "net10.0-windows");

        Assert.Contains(" -f 'net10.0-windows' ", command);
    }

    [Fact]
    public void BuildCommand_keeps_powershell_metacharacters_literal()
    {
        var command = CSharpBuildService.BuildCommand(
            @"C:\work\O'Brien $project.sln", "Debug $configuration");

        Assert.Equal(
            "dotnet build 'C:\\work\\O''Brien $project.sln' -c 'Debug $configuration' --nologo",
            command);
    }

    [Fact]
    public async Task RunAsync_delegates_to_visible_terminal()
    {
        var terminal = new RecordingTerminal();

        var result = await CSharpBuildService.RunAsync(terminal, @"C:\work\Sample.csproj", "Debug");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "dotnet build 'C:\\work\\Sample.csproj' -c 'Debug' --nologo",
            terminal.LastCommand);
        Assert.True(terminal.UsedVisibleTerminal);
    }

    private sealed class RecordingTerminal : ITerminalService
    {
        public string CurrentDirectory => @"C:\work";
        public bool IsExecuting => false;
        public string LastCommand { get; private set; } = "";
        public bool UsedVisibleTerminal { get; private set; }

        public Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
            => Task.FromResult(new CommandResult(command, "", 0, CurrentDirectory, true));

        public Task<CommandResult> RunCommandInVisibleTerminalAsync(string command, CancellationToken ct)
        {
            UsedVisibleTerminal = true;
            LastCommand = command;
            return RunCommandAsync(command, ct);
        }

        public void SetWorkingDirectory(string path) { }
        public bool TryRunInVisibleTerminal(string command) => false;
#pragma warning disable CS0067
        public event EventHandler<CommandResult>? CommandExecuted;
#pragma warning restore CS0067
    }
}
