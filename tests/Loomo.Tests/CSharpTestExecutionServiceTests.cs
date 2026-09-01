using System.IO;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.CSharp.Testing;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpTestExecutionServiceTests
{
    [Fact]
    public void Test_command_quotes_paths_configuration_and_filter()
    {
        var command = CSharpTestExecutionService.BuildTestCommand(
            @"C:\work\O'Brien Tests\Tests.csproj",
            "FullyQualifiedName=Tests.A|FullyQualifiedName=Tests.B",
            "Debug x",
            @"C:\temp\Loomo\test-results");

        Assert.Contains("dotnet test 'C:\\work\\O''Brien Tests\\Tests.csproj'", command);
        Assert.Contains("-c 'Debug x'", command);
        Assert.Contains("-f 'net10.0'", CSharpTestExecutionService.BuildTestCommand(
            @"C:\work\Tests.csproj", null, "Debug", @"C:\temp\results", "net10.0"));
        Assert.Contains("--filter 'FullyQualifiedName=Tests.A|FullyQualifiedName=Tests.B'", command);
        Assert.Contains("--results-directory 'C:\\temp\\Loomo\\test-results'", command);
    }

    [Fact]
    public void Coverage_command_contains_collector_and_redirected_output()
    {
        var command = CSharpTestExecutionService.BuildCoverageCommand(
            @"C:\work\Tests.csproj", "Release", @"C:\temp\coverage");

        Assert.Contains("--collect:\"XPlat Code Coverage\"", command);
        Assert.Contains("-f 'net10.0'", CSharpTestExecutionService.BuildCoverageCommand(
            @"C:\work\Tests.csproj", "Release", @"C:\temp\coverage", "net10.0"));
        Assert.Contains("/p:BaseOutputPath=artifacts/loomo-test/", command);
        Assert.Contains("--results-directory 'C:\\temp\\coverage'", command);
    }

    [Fact]
    public void List_tests_command_uses_the_shared_test_output_redirect()
    {
        var command = CSharpTestExecutionService.BuildListTestsCommand(
            @"C:\work space\Sample.csproj", "Release");

        Assert.Contains("--list-tests", command);
        Assert.Contains("'C:\\work space\\Sample.csproj'", command);
        Assert.Contains("/p:BaseOutputPath=artifacts/loomo-test/", command);
        Assert.Contains("-f 'net10.0'", CSharpTestExecutionService.BuildListTestsCommand(
            @"C:\work\Sample.csproj", "Release", "net10.0"));
    }

    [Fact]
    public void Fully_qualified_name_filter_deduplicates_methods_and_ignores_empty_names()
    {
        var filter = CSharpTestExecutionService.BuildFullyQualifiedNameFilter(
            ["Demo.Tests.A.Test", "", "Demo.Tests.A.Test", "Demo.Tests.B.Test"]);

        Assert.Equal(
            "FullyQualifiedName=Demo.Tests.A.Test|FullyQualifiedName=Demo.Tests.B.Test",
            filter);
    }

    [Fact]
    public async Task Run_executes_visible_terminal_and_returns_command_result()
    {
        var terminal = new CapturingTerminal();
        var result = await CSharpTestExecutionService.RunAsync(
            terminal, @"C:\work\Tests.csproj", null, "Debug");

        Assert.NotNull(terminal.Command);
        Assert.Contains("dotnet test 'C:\\work\\Tests.csproj'", terminal.Command);
        Assert.NotNull(result.Command);
        Assert.Null(result.PreparationError);
        Assert.NotNull(result.ResultsDirectory);
        CSharpTestExecutionService.CleanupResults(result);
        Assert.False(Directory.Exists(result.ResultsDirectory));
    }

    [Fact]
    public async Task Each_run_uses_an_isolated_results_directory()
    {
        var first = await CSharpTestExecutionService.RunAsync(
            new CapturingTerminal(), @"C:\work\Tests.csproj", null, "Debug");
        var second = await CSharpTestExecutionService.RunAsync(
            new CapturingTerminal(), @"C:\work\Tests.csproj", null, "Debug");

        Assert.NotEqual(first.ResultsDirectory, second.ResultsDirectory);
        Assert.NotNull(first.ResultsDirectory);
        Assert.NotNull(second.ResultsDirectory);
        Assert.True(Directory.Exists(first.ResultsDirectory));
        Assert.True(Directory.Exists(second.ResultsDirectory));
        CSharpTestExecutionService.CleanupResults(first);
        CSharpTestExecutionService.CleanupResults(second);
    }

    [Fact]
    public async Task Terminal_failure_cleans_up_the_run_directory()
    {
        var terminal = new ThrowingTerminal();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CSharpTestExecutionService.RunAsync(
                terminal, @"C:\work\Tests.csproj", null, "Debug"));

        Assert.NotNull(terminal.ResultsDirectory);
        Assert.False(Directory.Exists(terminal.ResultsDirectory));
    }

    [Fact]
    public async Task Coverage_run_uses_an_isolated_directory_and_can_be_cleaned_up()
    {
        var terminal = new CapturingTerminal();
        var result = await CSharpTestExecutionService.RunCoverageAsync(
            terminal, @"C:\work\Tests.csproj", "Debug");

        Assert.NotNull(result.Command);
        Assert.Contains("--collect:\"XPlat Code Coverage\"", terminal.Command);
        Assert.True(Directory.Exists(result.ResultsDirectory));

        CSharpTestExecutionService.CleanupCoverageResults(result.ResultsDirectory);

        Assert.False(Directory.Exists(result.ResultsDirectory));
    }

    [Fact]
    public async Task Coverage_terminal_failure_cleans_up_the_run_directory()
    {
        var terminal = new ThrowingTerminal();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CSharpTestExecutionService.RunCoverageAsync(
                terminal, @"C:\work\Tests.csproj", "Debug"));

        Assert.NotNull(terminal.ResultsDirectory);
        Assert.False(Directory.Exists(terminal.ResultsDirectory));
    }

    [Fact]
    public void Cleanup_does_not_delete_a_directory_outside_the_owned_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "Loomo-unowned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            CSharpTestExecutionService.CleanupResults(
                new CSharpTestExecutionResult(null, null, ResultsDirectory: outside));

            Assert.True(Directory.Exists(outside));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    [Fact]
    public void Coverage_cleanup_does_not_delete_a_directory_outside_the_owned_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "Loomo-unowned-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            CSharpTestExecutionService.CleanupCoverageResults(outside);

            Assert.True(Directory.Exists(outside));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    private class CapturingTerminal : ITerminalService
    {
        public string? Command { get; protected set; }
        public string CurrentDirectory => Environment.CurrentDirectory;
        public bool IsExecuting => false;

        public Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
            => RunCommandInVisibleTerminalAsync(command, ct);

        public virtual Task<CommandResult> RunCommandInVisibleTerminalAsync(string command, CancellationToken ct)
        {
            Command = command;
            return Task.FromResult(new CommandResult(command, "", 0, CurrentDirectory, true));
        }

        public void SetWorkingDirectory(string path) { }
        public bool TryRunInVisibleTerminal(string command) => false;

#pragma warning disable CS0067
        public event EventHandler<CommandResult>? CommandExecuted;
#pragma warning restore CS0067
    }

    private sealed class ThrowingTerminal : CapturingTerminal
    {
        public override Task<CommandResult> RunCommandInVisibleTerminalAsync(string command, CancellationToken ct)
        {
            Command = command;
            var marker = "--results-directory '";
            var start = command.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = command.IndexOf('\'', start);
            ResultsDirectory = command[start..end].Replace("''", "'", StringComparison.Ordinal);
            throw new OperationCanceledException(ct);
        }

        public string? ResultsDirectory { get; private set; }
    }
}
