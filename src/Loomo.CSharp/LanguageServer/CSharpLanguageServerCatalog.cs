using System.IO;
using System.Linq;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.CSharp.LanguageServer;

/// <summary>C#編集に使うRoslyn Language Serverの定義と、旧C#サーバー設定の移行判定。</summary>
public static class CSharpLanguageServerCatalog
{
    /// <summary>Loomoで検証済みのRoslyn Language Serverの固定版。</summary>
    public const string RoslynVersion = "5.9.0-1.26303.1";

    /// <summary>dotnet global toolが作るPATH上のシム名。</summary>
    public const string RoslynExecutable = "roslyn-language-server";

    public static readonly string[] RoslynArgs =
    [
        "--stdio",
        "--autoLoadProjects",
        "--telemetryLevel", "off",
    ];

    public static readonly string RoslynInstallCommand =
        "dotnet tool update --global roslyn-language-server --version " + RoslynVersion
        + "; if ($LASTEXITCODE -ne 0) { dotnet tool install --global roslyn-language-server --version "
        + RoslynVersion + " }";

    /// <summary>現在のRoslynシム定義と完全一致するC#サーバー設定か。</summary>
    public static bool IsRoslyn(string extension, LspServerDef server) =>
        string.Equals(LspExtensions.NormalizeExt(extension), ".cs", StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeExecutable(server.Executable), NormalizeExecutable(RoslynExecutable),
            StringComparison.Ordinal)
        && server.Args.SequenceEqual(RoslynArgs, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 組み込みRoslynへ置き換えるべき旧C#サーバー設定か。現在のシム名に独自引数を設定したものは
    /// ユーザーの意図した上書きなので移行対象にしない。
    /// </summary>
    public static bool IsSuperseded(string extension, LspServerDef server)
    {
        if (!string.Equals(LspExtensions.NormalizeExt(extension), ".cs", StringComparison.OrdinalIgnoreCase))
            return false;

        var executable = NormalizeExecutable(Path.GetFileName(server.Executable));
        if (executable == "csharp-ls") return true;
        if (IsLegacyLoomoRoslyn(server) || IsLegacyRoslynStorePath(server)) return true;
        if (executable != NormalizeExecutable(RoslynExecutable)) return false;
        return server.Args.Length == 0 || IsRoslyn(extension, server);
    }

    private static bool IsLegacyRoslynStorePath(LspServerDef server) =>
        NormalizeExecutable(Path.GetFileName(server.Executable)) == "microsoft.codeanalysis.languageserver"
        && server.Executable.Contains(
            Path.Combine(".store", "roslyn-language-server"), StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyLoomoRoslyn(LspServerDef server) =>
        NormalizeExecutable(Path.GetFileName(server.Executable)) == "dotnet"
        && server.Args.Any(argument => argument.EndsWith(
            "Microsoft.CodeAnalysis.LanguageServer.dll", StringComparison.OrdinalIgnoreCase))
        && server.Args.Any(argument => argument.Contains(
            $"{Path.DirectorySeparatorChar}Loomo{Path.DirectorySeparatorChar}lsp{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase));

    private static string NormalizeExecutable(string executable)
    {
        var name = executable.Trim();
        foreach (var suffix in new[] { ".exe", ".cmd", ".bat", ".ps1" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
        return name.ToLowerInvariant();
    }
}
