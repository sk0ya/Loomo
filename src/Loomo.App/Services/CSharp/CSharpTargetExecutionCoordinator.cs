using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.CSharp.Build;
using sk0ya.Loomo.CSharp.Debug;
using sk0ya.Loomo.CSharp.Testing;

namespace sk0ya.Loomo.App.Services;

internal sealed record CSharpTargetExecutionOutcome(
    CommandResult? Command,
    CSharpTestExecutionResult? TestExecution,
    string? Error);

/// <summary>Solution ExplorerとDebug switcherから使うC# build/test/runのtarget選択と実行調整。</summary>
internal static class CSharpTargetExecutionCoordinator
{
    internal static async Task<CSharpTargetExecutionOutcome> ExecuteAsync(
        ITerminalService terminal,
        string target,
        CSharpSolutionAction action,
        string configuration,
        string? targetFramework,
        LaunchSettingsProfile? launchProfile)
    {
        if (launchProfile?.IsIisExpress == true)
        {
            var command = IisExpressLaunchCommand.Build(target, launchProfile, out var launchError);
            if (command is null)
                return new(null, null, $"IIS Expressを起動できません: {launchError}");
            var result = await terminal.RunCommandInVisibleTerminalAsync(command, CancellationToken.None);
            return new(result, null, null);
        }

        if (action == CSharpSolutionAction.Build)
        {
            var result = await CSharpBuildService.RunAsync(
                terminal, target, configuration, CancellationToken.None, targetFramework);
            return new(result, null, null);
        }

        if (action == CSharpSolutionAction.Test)
        {
            var execution = await CSharpTestExecutionService.RunAsync(
                terminal, target, null, configuration, CancellationToken.None,
                targetFramework: targetFramework);
            if (execution.PreparationError is { } preparationError)
                return new(null, execution, preparationError);
            if (execution.Command is not { } testResult)
                return new(null, execution, "テストを実行できませんでした。");
            return new(testResult, execution, null);
        }

        var launchProfileName = launchProfile is { IsSupported: true, Name.Length: > 0 }
            ? launchProfile.Name
            : null;
        var runResult = await CSharpRunService.RunAsync(
            terminal, target, configuration, targetFramework, launchProfileName, CancellationToken.None);
        return new(runResult, null, null);
    }
}
