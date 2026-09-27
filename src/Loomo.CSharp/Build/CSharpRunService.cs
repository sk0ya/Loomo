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
        // 旧形式（非SDK）は dotnet run が使えない。MSBuild.exe でビルドし、TargetPath の exe をそのまま起動する。
        if (MsBuildToolchain.IsLegacyProject(projectPath) && MsBuildToolchain.FindVisualStudioMsBuild() is { } msbuild)
            return LegacyRunCommand(msbuild, projectPath, configuration);

        var framework = string.IsNullOrWhiteSpace(targetFramework)
            ? ""
            : " -f " + PowerShellQuote(targetFramework);
        var profile = string.IsNullOrWhiteSpace(launchProfile)
            ? " --no-launch-profile"
            : " --launch-profile " + PowerShellQuote(launchProfile);
        return "dotnet run --project " + PowerShellQuote(projectPath) +
            " -c " + PowerShellQuote(configuration) + framework + profile + " --nologo";
    }

    /// <summary>旧形式プロジェクトの実行。ビルドが通ったら <c>-getProperty:TargetPath</c> で出力先を MSBuild 自身に
    /// 聞いて起動する（AssemblyName・OutputPath・プラットフォーム別の出力先を Loomo 側で推測しない）。</summary>
    internal static string LegacyRunCommand(string msbuildPath, string projectPath, string configuration)
    {
        var msbuild = CSharpBuildService.PowerShellQuote(msbuildPath);
        var project = CSharpBuildService.PowerShellQuote(projectPath);
        var config = CSharpBuildService.PowerShellQuote(configuration);
        return CSharpBuildService.VisualStudioBuildCommand(msbuildPath, projectPath, configuration) +
            "; if ($LASTEXITCODE -eq 0) { $loomoTarget = (& " + msbuild + " " + project +
            " -getProperty:TargetPath -p:Configuration=" + config + CSharpBuildService.SolutionDirArgument(projectPath) +
            " -nologo | Select-Object -Last 1).Trim(); " +
            // 問い合わせが失敗すると MSBuild のエラー文が返る。それを実行しようとしないよう、ファイルがあるときだけ起動する。
            "if (Test-Path -LiteralPath $loomoTarget -PathType Leaf) { & $loomoTarget } " +
            "else { Write-Host ('ビルド出力が見つかりません: ' + $loomoTarget) } }";
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
