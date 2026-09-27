using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.CSharp.Build;

/// <summary>.NETプロジェクト／ソリューションのビルドコマンドを組み立てて実行するC#層のサービス。</summary>
public static class CSharpBuildService
{
    /// <summary>UIやセッション状態に依存しない、再現可能なビルドコマンドを返す。
    /// 旧形式（非SDK）のプロジェクトを含む対象は Visual Studio の MSBuild.exe で回す（<see cref="MsBuildToolchain"/>）。</summary>
    public static string BuildCommand(string projectOrSolution, string configuration = "Debug",
        string? targetFramework = null)
        => BuildCommand(projectOrSolution, configuration, targetFramework, MsBuildToolchain.For(projectOrSolution),
            MsBuildToolchain.ContainsLegacyProject(projectOrSolution));

    /// <summary><see cref="BuildCommand(string,string,string?)"/> の本体。使う MSBuild を外から渡せる形（テスト用）。</summary>
    internal static string BuildCommand(string projectOrSolution, string configuration, string? targetFramework,
        MsBuildInvocation msbuild, bool containsLegacyProject)
    {
        if (msbuild.IsVisualStudio)
            return VisualStudioBuildCommand(msbuild.FileName, projectOrSolution, configuration);

        var framework = string.IsNullOrWhiteSpace(targetFramework) || containsLegacyProject
            ? ""
            : " -f " + PowerShellQuote(targetFramework);
        var command = "dotnet build " + PowerShellQuote(projectOrSolution) +
            " -c " + PowerShellQuote(configuration) + framework + " --nologo";
        // 旧形式なのに MSBuild.exe が無い。dotnet でも通るプロジェクトはあるので試すが、落ちたときに
        // 理由が分かるよう先に一言出しておく（packages.config・COM 参照・VS 専用 targets は dotnet では通らない）。
        return containsLegacyProject
            ? "Write-Host " + PowerShellQuote(
                "旧形式のプロジェクトですが Visual Studio / Build Tools の MSBuild.exe が見つからないため、dotnet build で試みます。") +
              "; " + command
            : command;
    }

    /// <summary>MSBuild.exe でのビルド。<c>-restore</c> と <c>RestorePackagesConfig</c> で packages.config も
    /// 同じ1回で復元する（NuGet.exe を別に要らない）。旧形式に TFM の切り替えは無いので <c>-f</c> 相当は付けない。</summary>
    internal static string VisualStudioBuildCommand(string msbuildPath, string projectOrSolution, string configuration)
        => "& " + PowerShellQuote(msbuildPath) + " " + PowerShellQuote(projectOrSolution) +
           " -restore -p:RestorePackagesConfig=true -p:Configuration=" + PowerShellQuote(configuration) +
           SolutionDirSwitch(projectOrSolution) + " -m -nologo -v:minimal";

    /// <summary>プロジェクト単体のビルドにだけ <c>SolutionDir</c> を渡す（<see cref="MsBuildToolchain.SolutionDirectoryFor"/>）。
    /// 末尾の区切りは <c>/</c>——<c>\</c> で終えると Windows PowerShell 5.1 がネイティブ引数を引用するとき
    /// <c>\"</c> になって閉じ引用符が消える。MSBuild と NuGet はどちらの区切りでも同じに扱う。</summary>
    private static string SolutionDirSwitch(string projectOrSolution)
    {
        var extension = Path.GetExtension(projectOrSolution);
        if (!extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase)) return "";
        var directory = MsBuildToolchain.SolutionDirectoryFor(projectOrSolution).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return " -p:SolutionDir=" + PowerShellQuote(directory + "/");
    }

    /// <summary>実行コマンドの TargetPath 問い合わせにも、ビルドと同じ SolutionDir を渡すためのもの。</summary>
    internal static string SolutionDirArgument(string project) => SolutionDirSwitch(project);

    /// <summary>ビルド出力は、C#編集画面と分離された人間向けの表示ターミナルへ送る。</summary>
    public static Task<CommandResult> RunAsync(ITerminalService terminal, string projectOrSolution,
        string configuration = "Debug", CancellationToken cancellationToken = default,
        string? targetFramework = null)
        => terminal.RunCommandInVisibleTerminalAsync(
            BuildCommand(projectOrSolution, configuration, targetFramework), cancellationToken);

    internal static string PowerShellQuote(string value)
        => "'" + (value ?? "").Replace("'", "''", StringComparison.Ordinal) + "'";
}
