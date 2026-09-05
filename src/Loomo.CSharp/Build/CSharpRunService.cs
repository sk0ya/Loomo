using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.CSharp.Build;

/// <summary>C#プロジェクトの通常実行コマンドを組み立てて実行するサービス。
/// launch profileの選択やTargetFrameworkはUIではなくC#実行層で一貫して扱う。</summary>
public static class CSharpRunService
{
    public static string BuildCommand(
        string projectPath,
        string configuration = "Debug",
        string? targetFramework = null,
        string? launchProfile = null)
    {
        var framework = string.IsNullOrWhiteSpace(targetFramework)
            ? ""
            : " -f " + PowerShellQuote(targetFramework);
        var profile = string.IsNullOrWhiteSpace(launchProfile)
            ? " --no-launch-profile"
            : " --launch-profile " + PowerShellQuote(launchProfile);
        return "dotnet run --project " + PowerShellQuote(projectPath) +
            " -c " + PowerShellQuote(configuration) + framework + profile + " --nologo";
    }

    public static Task<CommandResult> RunAsync(
        ITerminalService terminal,
        string projectPath,
        string configuration = "Debug",
        string? targetFramework = null,
        string? launchProfile = null,
        CancellationToken cancellationToken = default)
        => terminal.RunCommandInVisibleTerminalAsync(
            BuildCommand(projectPath, configuration, targetFramework, launchProfile),
            cancellationToken);

    private static string PowerShellQuote(string value)
        => "'" + (value ?? "").Replace("'", "''", StringComparison.Ordinal) + "'";
}
