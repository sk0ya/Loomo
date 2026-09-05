using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.CSharp.Build;

/// <summary>.NETプロジェクト／ソリューションのビルドコマンドを組み立てて実行するC#層のサービス。</summary>
public static class CSharpBuildService
{
    /// <summary>UIやセッション状態に依存しない、再現可能なビルドコマンドを返す。</summary>
    public static string BuildCommand(string projectOrSolution, string configuration = "Debug",
        string? targetFramework = null)
    {
        var framework = string.IsNullOrWhiteSpace(targetFramework)
            ? ""
            : " -f " + PowerShellQuote(targetFramework);
        return "dotnet build " + PowerShellQuote(projectOrSolution) +
            " -c " + PowerShellQuote(configuration) + framework + " --nologo";
    }

    /// <summary>ビルド出力は、C#編集画面と分離された人間向けの表示ターミナルへ送る。</summary>
    public static Task<CommandResult> RunAsync(ITerminalService terminal, string projectOrSolution,
        string configuration = "Debug", CancellationToken cancellationToken = default,
        string? targetFramework = null)
        => terminal.RunCommandInVisibleTerminalAsync(
            BuildCommand(projectOrSolution, configuration, targetFramework), cancellationToken);

    private static string PowerShellQuote(string value)
        => "'" + (value ?? "").Replace("'", "''", StringComparison.Ordinal) + "'";
}
