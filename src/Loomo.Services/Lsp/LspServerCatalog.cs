using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.LanguageServer;

namespace sk0ya.Loomo.Services.Lsp;

/// <summary>サーバーが受け持つ拡張子1件と、そこで名乗る LSP の languageId。</summary>
public sealed record LspServerTarget(string Extension, string LanguageId);

/// <summary>
/// 既知の言語サーバー1件分。実行ファイル・引数・受け持つ拡張子と languageId・表示名・
/// インストールコマンド・導入手順URLを**1レコードに**まとめる。
/// 以前は「実行ファイルはエディタの組み込み表、インストール手順は Loomo のカタログ」と割れていて
/// 片方だけ更新される事故が起きたので、<see cref="LspServerTable.Builtins"/> はここから導出する。
/// </summary>
public sealed record LspServerInfo(
    string Executable,
    string DisplayName,
    IReadOnlyList<LspServerTarget> Targets,
    string? InstallCommand,
    string[] Args,
    string? DocsUrl = null)
{
    /// <summary>受け持つ拡張子（表示・照合用）。</summary>
    public IReadOnlyList<string> Extensions => Targets.Select(t => t.Extension).ToList();

    /// <summary>代表 languageId（拡張子が判っているときは <see cref="LanguageIdFor"/> を使うこと）。</summary>
    public string LanguageId => Targets[0].LanguageId;

    /// <summary>この拡張子で名乗る languageId。受け持たない拡張子なら代表値。</summary>
    public string LanguageIdFor(string extension) =>
        Targets.FirstOrDefault(t =>
            string.Equals(t.Extension, extension, StringComparison.OrdinalIgnoreCase))?.LanguageId
        ?? LanguageId;
}

/// <summary>
/// Loomo が知っている言語サーバーのカタログ。<see cref="LspServerTable"/> の組み込み既定表はここから
/// 導出されるので、ここが「どの拡張子をどの実行ファイルがどう名乗って受け持つか」の唯一の出所になる。
/// </summary>
public static class LspServerCatalog
{
    //  C#固有のRoslyn定義は Loomo.CSharp.LanguageServer が所有し、ここでは多言語カタログへ写像する。
    internal const string RoslynVersion = CSharpLanguageServerCatalog.RoslynVersion;
    internal const string RoslynExecutable = CSharpLanguageServerCatalog.RoslynExecutable;
    internal static readonly string[] RoslynArgs = CSharpLanguageServerCatalog.RoslynArgs;

    /// <summary>winget 等の Windows 向けを優先した、ベストエフォートのインストールコマンド付きカタログ。</summary>
    public static readonly IReadOnlyList<LspServerInfo> Servers = new[]
    {
        new LspServerInfo(CSharpLanguageServerCatalog.RoslynExecutable, "C# (Roslyn Language Server)",
            [new(".cs", "csharp")],
            CSharpLanguageServerCatalog.RoslynInstallCommand, CSharpLanguageServerCatalog.RoslynArgs,
            "https://github.com/dotnet/roslyn"),
        // ESM/CJS 明示の拡張子（.mts/.cts/.mjs/.cjs）も同じ tsserver が扱う。languageId は VS Code の
        // 組み込み定義に合わせる（typescript ← .ts/.mts/.cts、javascript ← .js/.mjs/.cjs）。
        // 促し対象（LspManagementService.PromptableSourceExtensions）にはあるのにカタログに候補が無く、
        // 「言語サーバーが設定されていません」としか出せていなかった穴を塞ぐ。
        new LspServerInfo("typescript-language-server", "TypeScript / JavaScript",
            [new(".ts", "typescript"), new(".mts", "typescript"), new(".cts", "typescript"),
             new(".tsx", "typescriptreact"),
             new(".js", "javascript"), new(".mjs", "javascript"), new(".cjs", "javascript"),
             new(".jsx", "javascriptreact")],
            "npm install -g typescript-language-server typescript", ["--stdio"],
            "https://github.com/typescript-language-server/typescript-language-server"),
        // Svelte 公式（sveltejs/language-tools）。npm パッケージ名とシム名が違う（svelteserver）ことに注意。
        // .vue は**あえて入れていない** — 公式の @vue/language-server は tsdk パスなどの
        // initializationOptions が要り、LspServerDef（実行ファイル＋引数＋languageId）では表現できない。
        new LspServerInfo("svelteserver", "Svelte (svelte-language-server)",
            [new(".svelte", "svelte")],
            "npm install -g svelte-language-server", ["--stdio"],
            "https://github.com/sveltejs/language-tools/tree/master/packages/language-server"),
        new LspServerInfo("pylsp", "Python (python-lsp-server)",
            [new(".py", "python")],
            "pip install python-lsp-server", [],
            "https://github.com/python-lsp/python-lsp-server"),
        new LspServerInfo("rust-analyzer", "Rust (rust-analyzer)",
            [new(".rs", "rust")],
            "rustup component add rust-analyzer", [],
            "https://rust-analyzer.github.io/"),
        new LspServerInfo("gopls", "Go (gopls)",
            [new(".go", "go")],
            "go install golang.org/x/tools/gopls@latest", [],
            "https://pkg.go.dev/golang.org/x/tools/gopls"),
        new LspServerInfo("clangd", "C / C++ (clangd)",
            [new(".c", "c"), new(".h", "c"), new(".cpp", "cpp"), new(".hpp", "cpp")],
            "winget install --id LLVM.LLVM -e", [],
            "https://clangd.llvm.org/installation"),
        new LspServerInfo("lua-language-server", "Lua (lua-language-server)",
            [new(".lua", "lua")],
            "winget install --id LuaLS.lua-language-server -e", [],
            "https://github.com/LuaLS/lua-language-server"),
        new LspServerInfo("solargraph", "Ruby (solargraph)",
            [new(".rb", "ruby")],
            "gem install solargraph", ["stdio"],
            "https://solargraph.org/"),
        new LspServerInfo("marksman", "Markdown (marksman)",
            [new(".md", "markdown"), new(".markdown", "markdown")],
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
        var ext = LspExtensions.NormalizeExt(extension);
        return Servers
            .Where(s => s.Targets.Any(t => string.Equals(t.Extension, ext, StringComparison.OrdinalIgnoreCase)))
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

    /// <summary>旧C#サーバー設定の判定はCSharp専用DLLへ委譲する。</summary>
    internal static bool IsSupersededCSharpServer(string extension, LspServerDef server)
        => CSharpLanguageServerCatalog.IsSuperseded(extension, server);

    internal static bool IsRoslynCSharp(string extension, LspServerDef server) =>
        CSharpLanguageServerCatalog.IsRoslyn(extension, server);

    internal static LspServerDef RoslynCSharpDefinition() =>
        new(CSharpLanguageServerCatalog.RoslynExecutable, CSharpLanguageServerCatalog.RoslynArgs, "csharp");
}
