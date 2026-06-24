using System;
using System.Collections.Generic;
using System.Linq;

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
    /// <summary>winget 等の Windows 向けを優先した、ベストエフォートのインストールコマンド付きカタログ。</summary>
    public static readonly IReadOnlyList<LspServerInfo> Servers = new[]
    {
        new LspServerInfo("csharp-ls", "C# (csharp-ls)", [".cs"], "csharp",
            "dotnet tool install --global csharp-ls", [],
            "https://github.com/razzmatazz/csharp-language-server"),
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
            "winget install --id Marksman.Marksman -e", ["server"],
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
}
