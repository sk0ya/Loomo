using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Services.Lsp;

/// <summary>
/// 既知の言語サーバー1件分のメタ情報。<see cref="Executable"/> をキーに、エディタの
/// <c>LspServerRegistry</c>（拡張子→実行ファイルの対応表）と突き合わせて、設定UIに
/// 「表示名・対象拡張子・インストールコマンド・導入手順URL」を補う。
/// </summary>
public sealed record LspServerInfo(
    string Executable,
    string DisplayName,
    string[] Extensions,
    string LanguageId,
    string? InstallCommand,
    string[] Args,
    string? DocsUrl = null);

/// <summary>
/// Loomo が知っている言語サーバーのカタログ。エディタの組み込み対応表と同じ実行ファイルを並べ、
/// 各サーバーの**インストールコマンド**（Loomo から見えるターミナルで実行する用）と導入手順URLを持つ。
/// 「どの実行ファイルをどの拡張子に割り当てるか」はエディタ側 <c>LspServerRegistry</c> が所有するが、
/// 「どうやって入れるか」はアプリ＝Loomo の関心なのでここに置く。
/// </summary>
public static class LspServerCatalog
{
    /// <summary>
    /// Microsoft が MIT で配布する Roslyn Language Server の、Loomo で検証済みの固定版。
    /// VS Code 拡張の配布物（利用先制限あり）は流用せず、MIT 指定の NuGet パッケージを直接取得する。
    /// DevKit / XAML Tools は再配布不可の Microsoft 独自 DLL を含むため読み込まない。
    /// </summary>
    internal const string RoslynVersion = "5.9.0-1.26303.1";
    internal static readonly string RoslynExecutable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dotnet", "tools", ".store", "roslyn-language-server", RoslynVersion,
        "roslyn-language-server.win-x64", RoslynVersion, "tools", "net10.0", "win-x64",
        "Microsoft.CodeAnalysis.LanguageServer.exe");

    internal static readonly string[] RoslynArgs =
    [
        "--stdio",
        "--autoLoadProjects",
        "--telemetryLevel", "off",
    ];

    // update は既存ツールを固定版へ揃える。未導入なら失敗後に install する。
    private const string RoslynInstallCommand =
        "dotnet tool update --global roslyn-language-server --version " + RoslynVersion
        + "; if ($LASTEXITCODE -ne 0) { dotnet tool install --global roslyn-language-server --version "
        + RoslynVersion + " }";

    /// <summary>winget 等の Windows 向けを優先した、ベストエフォートのインストールコマンド付きカタログ。</summary>
    public static readonly IReadOnlyList<LspServerInfo> Servers = new[]
    {
        new LspServerInfo(RoslynExecutable, "C# (Roslyn Language Server)", [".cs"], "csharp",
            RoslynInstallCommand, RoslynArgs,
            "https://github.com/dotnet/roslyn"),
        new LspServerInfo("typescript-language-server", "TypeScript / JavaScript",
            [".ts", ".tsx", ".js", ".jsx"], "typescript",
            "npm install -g typescript-language-server typescript", ["--stdio"],
            "https://github.com/typescript-language-server/typescript-language-server"),
        new LspServerInfo("pylsp", "Python (python-lsp-server)", [".py"], "python",
            "pip install python-lsp-server", [],
            "https://github.com/python-lsp/python-lsp-server"),
        new LspServerInfo("rust-analyzer", "Rust (rust-analyzer)", [".rs"], "rust",
            "rustup component add rust-analyzer", [],
            "https://rust-analyzer.github.io/"),
        new LspServerInfo("gopls", "Go (gopls)", [".go"], "go",
            "go install golang.org/x/tools/gopls@latest", [],
            "https://pkg.go.dev/golang.org/x/tools/gopls"),
        new LspServerInfo("clangd", "C / C++ (clangd)", [".c", ".cpp", ".h", ".hpp"], "cpp",
            "winget install --id LLVM.LLVM -e", [],
            "https://clangd.llvm.org/installation"),
        new LspServerInfo("lua-language-server", "Lua (lua-language-server)", [".lua"], "lua",
            "winget install --id LuaLS.lua-language-server -e", [],
            "https://github.com/LuaLS/lua-language-server"),
        new LspServerInfo("solargraph", "Ruby (solargraph)", [".rb"], "ruby",
            "gem install solargraph", ["stdio"],
            "https://solargraph.org/"),
        new LspServerInfo("marksman", "Markdown (marksman)", [".md", ".markdown"], "markdown",
            "winget install --id Artempyanykh.Marksman -e", ["server"],
            "https://github.com/artempyanykh/marksman"),
    };

    /// <summary>実行ファイル名（拡張子の有無は無視）でカタログ項目を引く。無ければ null。</summary>
    public static LspServerInfo? ByExecutable(string executable)
    {
        var name = NormalizeExe(executable);
        return Servers.FirstOrDefault(s => NormalizeExe(s.Executable) == name);
    }

    /// <summary>その拡張子に対応するカタログ項目（インストール候補）を返す。無ければ空。</summary>
    public static IReadOnlyList<LspServerInfo> ByExtension(string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return Servers
            .Where(s => s.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static string NormalizeExe(string exe)
    {
        var name = exe.Trim();
        foreach (var suffix in new[] { ".exe", ".cmd", ".bat", ".ps1" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Editor パッケージの旧組み込み値（csharp-ls）と、旧Loomo専用配置を
    /// Roslynグローバルツールへ移行する。
    /// ユーザーが明示設定した C# サーバーは上書きしない。
    /// </summary>
    public static void EnsureCSharpDefault(LspServerRegistry registry)
    {
        var row = registry.List().FirstOrDefault(e =>
            string.Equals(e.Extension, ".cs", StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return;

        var isOldBuiltIn = row.Origin == LspServerOrigin.BuiltIn
            && string.Equals(NormalizeExe(row.Server.Executable), "csharp-ls", StringComparison.Ordinal);
        var isOldGlobalShim = string.Equals(
            NormalizeExe(row.Server.Executable), "roslyn-language-server", StringComparison.Ordinal);
        if (!isOldBuiltIn && !isOldGlobalShim && !IsLegacyLoomoRoslyn(row.Server))
            return;

        registry.Set(".cs", RoslynCSharpDefinition());
    }

    private static bool IsLegacyLoomoRoslyn(LspServerDef server) =>
        NormalizeExe(Path.GetFileName(server.Executable)) == "dotnet"
        && server.Args.Any(a => a.EndsWith(
            "Microsoft.CodeAnalysis.LanguageServer.dll",
            StringComparison.OrdinalIgnoreCase))
        && server.Args.Any(a => a.Contains(
            $"{Path.DirectorySeparatorChar}Loomo{Path.DirectorySeparatorChar}lsp{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase));

    internal static bool IsRoslynCSharp(string extension, LspServerDef server) =>
        string.Equals(LspServerRegistry.NormalizeExt(extension), ".cs", StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            NormalizeExe(server.Executable),
            NormalizeExe(RoslynExecutable),
            StringComparison.Ordinal)
        && server.Args.SequenceEqual(RoslynArgs, StringComparer.OrdinalIgnoreCase);

    internal static LspServerDef RoslynCSharpDefinition() =>
        new(RoslynExecutable, RoslynArgs, "csharp");
}
