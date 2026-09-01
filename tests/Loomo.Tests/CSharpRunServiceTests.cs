using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.CSharp.Build;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpRunServiceTests
{
    [Fact]
    public void BuildCommand_uses_safe_quotes_and_selected_framework_and_profile()
    {
        var command = CSharpRunService.BuildCommand(
            @"C:\work\O'Brien App.csproj", "Release x", "net10.0", "Local Dev");

        Assert.Equal(
            "dotnet run --project 'C:\\work\\O''Brien App.csproj' -c 'Release x' -f 'net10.0' --launch-profile 'Local Dev' --nologo",
            command);
    }

    [Fact]
    public void BuildCommand_disables_launch_profile_when_none_is_selected()
    {
        var command = CSharpRunService.BuildCommand(@"C:\work\App.csproj");

        Assert.Contains("--no-launch-profile", command);
        Assert.DoesNotContain("--launch-profile '", command);
    }

    [Fact]
    public async Task RunAsync_uses_the_visible_terminal()
    {
        var terminal = new CapturingTerminal();

        var result = await CSharpRunService.RunAsync(
            terminal, @"C:\work\App.csproj", "Debug", "net10.0", "Local");

        Assert.Equal(0, result.ExitCode);
        Assert.True(terminal.UsedVisibleTerminal);
        Assert.Contains("dotnet run --project 'C:\\work\\App.csproj'", terminal.Command);
        Assert.Contains("--launch-profile 'Local'", terminal.Command);
    }

    private sealed class CapturingTerminal : ITerminalService
    {
        public string CurrentDirectory => @"C:\work";
        public bool IsExecuting => false;
        public string Command { get; private set; } = "";
        public bool UsedVisibleTerminal { get; private set; }

        public Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
            => Task.FromResult(new CommandResult(command, "", 0, CurrentDirectory, true));

        public Task<CommandResult> RunCommandInVisibleTerminalAsync(string command, CancellationToken ct)
        {
            Command = command;
            UsedVisibleTerminal = true;
            return RunCommandAsync(command, ct);
        }

        public void SetWorkingDirectory(string path) { }
        public bool TryRunInVisibleTerminal(string command) => false;

#pragma warning disable CS0067
        public event EventHandler<CommandResult>? CommandExecuted;
#pragma warning restore CS0067
    }
}
