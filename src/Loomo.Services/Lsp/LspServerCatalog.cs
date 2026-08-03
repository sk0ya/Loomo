using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Lsp;

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
    /// <summary>
    /// Microsoft が MIT で配布する Roslyn Language Server の、Loomo で検証済みの固定版。
    /// VS Code 拡張の配布物（利用先制限あり）は流用せず、MIT 指定の NuGet パッケージを直接取得する。
    /// DevKit / XAML Tools は再配布不可の Microsoft 独自 DLL を含むため読み込まない。
    /// </summary>
    internal const string RoslynVersion = "5.9.0-1.26303.1";

    /// <summary>
    /// C# サーバーの実行ファイルは **dotnet グローバルツールのシム名**（＝PATH 上で解決できる名前）。
    /// <see cref="RoslynInstallCommand"/> が入れる <c>roslyn-language-server</c> は
    /// <c>%USERPROFILE%\.dotnet\tools\roslyn-language-server.cmd</c> を作り、そのディレクトリは PATH 上にある。
    /// シムは <c>%*</c> を実体（<c>Microsoft.CodeAnalysis.LanguageServer.exe</c>）へそのまま渡すので
    /// <see cref="RoslynArgs"/> はこれまでどおり効く。
    /// <para>以前はツールストア深部の**フルパス**を既定にしていたが、その名前は PATH 上に無く、
    /// 導入判定（<c>ExecutableResolver.IsOnPath</c>）が常に「未導入」を返していた。
    /// つまり**カタログの案内どおりインストールしても未導入のまま**という自己矛盾になっていた
    /// （フルパスはツール版が上がるたびに変わるので、版を固定した綴りを既定に持つこと自体が誤り）。</para>
    /// </summary>
    internal const string RoslynExecutable = "roslyn-language-server";

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
        new LspServerInfo(RoslynExecutable, "C# (Roslyn Language Server)",
            [new(".cs", "csharp")],
            RoslynInstallCommand, RoslynArgs,
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

    /// <summary>
    /// 永続化された <c>.cs</c> のユーザー設定が「組み込みが今の形になる前の遺物」かどうか。
    /// 旧 Editor 組み込み値（<c>csharp-ls</c>）・旧 Loomo 専用配置
    /// （<c>%APPDATA%/Loomo/lsp/…dotnet …LanguageServer.dll</c>）・
    /// 旧組み込みのツールストア深部フルパス（<see cref="IsLegacyRoslynStorePath"/>）が該当する。
    /// これらを残すと、組み込みを更新してもユーザー設定が勝ち続けて古いサーバーが起動してしまう。
    ///
    /// <para><c>roslyn-language-server</c>（シム名）を畳むのは<b>組み込みと完全に同じ（引数も一致）</b>か
    /// <b>引数が空</b>（旧綴りの遺物。<c>--stdio</c> すら無いので起動しても応答しない）のときだけ。
    /// **引数を変えた上書きは畳まない** —— 既定がシム名になった以降は「シム名＋独自の引数」が正当な設定に
    /// なりうる。実行ファイル名だけで畳むと、設定 UI から登録できてそのセッションでは効くのに次回起動で
    /// 消える、というサイレントな設定ロスになる（レビュー指摘 R4）。</para>
    /// </summary>
    internal static bool IsSupersededCSharpServer(string extension, LspServerDef server)
    {
        if (!string.Equals(LspExtensions.NormalizeExt(extension), ".cs", StringComparison.OrdinalIgnoreCase))
            return false;
        var exe = NormalizeExe(Path.GetFileName(server.Executable));
        if (exe == "csharp-ls") return true;
        if (IsLegacyLoomoRoslyn(server) || IsLegacyRoslynStorePath(server)) return true;
        if (exe != "roslyn-language-server") return false;
        // シム名で畳むのは2通りだけ：組み込みと完全に同一（＝抱える意味が無い）か、
        // 引数が空（＝旧綴りの遺物。--stdio すら無いので起動しても応答しない）。
        // それ以外の「シム名＋独自の引数」は正当な上書きとして残す。
        return server.Args.Length == 0 || IsRoslynCSharp(extension, server);
    }

    /// <summary>
    /// 旧組み込みの綴り（<c>%USERPROFILE%\.dotnet\tools\.store\roslyn-language-server\&lt;版&gt;\…\
    /// Microsoft.CodeAnalysis.LanguageServer.exe</c>）。PATH 上に無いうえ版を含むので、ツールを更新した
    /// 時点で存在しないパスになる。上書きとして残っていたら捨てて組み込み（シム名）へ戻す。
    /// </summary>
    private static bool IsLegacyRoslynStorePath(LspServerDef server) =>
        NormalizeExe(Path.GetFileName(server.Executable)) == "microsoft.codeanalysis.languageserver"
        && server.Executable.Contains(
            Path.Combine(".store", "roslyn-language-server"), StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyLoomoRoslyn(LspServerDef server) =>
        NormalizeExe(Path.GetFileName(server.Executable)) == "dotnet"
        && server.Args.Any(a => a.EndsWith(
            "Microsoft.CodeAnalysis.LanguageServer.dll",
            StringComparison.OrdinalIgnoreCase))
        && server.Args.Any(a => a.Contains(
            $"{Path.DirectorySeparatorChar}Loomo{Path.DirectorySeparatorChar}lsp{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase));

    internal static bool IsRoslynCSharp(string extension, LspServerDef server) =>
        string.Equals(LspExtensions.NormalizeExt(extension), ".cs", StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            NormalizeExe(server.Executable),
            NormalizeExe(RoslynExecutable),
            StringComparison.Ordinal)
        && server.Args.SequenceEqual(RoslynArgs, StringComparer.OrdinalIgnoreCase);

    internal static LspServerDef RoslynCSharpDefinition() =>
        new(RoslynExecutable, RoslynArgs, "csharp");
}
