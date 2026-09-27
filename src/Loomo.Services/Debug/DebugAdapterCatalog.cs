using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>
/// 既知のデバッグアダプタ 1 件分のメタ情報。LSP の <c>LspServerCatalog</c> と同じ発想で、
/// 「どの実行ファイルか・対象拡張子・インストールコマンド・導入手順URL」を保持し、未導入時に
/// 見えるターミナルでの導入を案内するために使う。
/// </summary>
public sealed record DebugAdapterInfo(
    string Executable,
    string DisplayName,
    string[] Extensions,
    string[] Args,
    string? InstallCommand,
    string? DocsUrl = null);

/// <summary>
/// Loomo が知っているデバッグアダプタのカタログ。Phase 1 は C#/.NET（<c>netcoredbg</c>）のみ。
/// アダプタは MS 純正 <c>vsdbg</c> ではなく OSS の <c>netcoredbg</c> を使う（vsdbg は VS/VS Code 外の利用が
/// ライセンス上禁止のため）。LSP と同様「PATH 上にあれば使える」方式で、未導入なら導入を促す。
/// </summary>
public static class DebugAdapterCatalog
{
    /// <summary>C#/.NET 用アダプタ。<c>--interpreter=vscode</c> で stdio 上の DAP モードになる。</summary>
    public static readonly DebugAdapterInfo Netcoredbg = new(
        "netcoredbg",
        "C# / .NET (netcoredbg)",
        [".cs"],
        ["--interpreter=vscode"],
        // Windows でのベストエフォート。scoop が無い環境もあるため DocsUrl で手動導入も案内する。
        "scoop install netcoredbg",
        "https://github.com/Samsung/netcoredbg/releases");

    /// <summary>TypeScript / JavaScript（Node.js）用アダプタ vscode-js-debug。実行ファイルは node で、
    /// 本体（dapDebugServer.js）は GitHub Releases からのダウンロード —— 導入判定・導入先・インストール
    /// コマンドの実体は <see cref="Js.JsDebugAdapterLocator"/> が持つ（PATH 判定だけでは足りないため）。</summary>
    public static readonly DebugAdapterInfo JsDebug = new(
        "node",
        "TypeScript / Node.js (vscode-js-debug)",
        [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"],
        [],
        Js.JsDebugAdapterLocator.InstallCommand,
        "https://github.com/microsoft/vscode-js-debug/releases");

    /// <summary>NetFx アダプタの置き場所を上書きする環境変数（テスト・開発用）。</summary>
    public const string NetFxDirectoryVariable = "LOOMO_NETFXDBG_DIR";

    /// <summary>.NET Framework（デスクトップ CLR）用アダプタの実行ファイル。netcoredbg／vsdbg はどちらも CoreCLR 専用で
    /// .NET Framework を扱えないため、ICorDebug を直接使う自前のアダプタ（<c>src/Loomo.NetFxDebug</c>）を
    /// Loomo に同梱している（導入は不要）。ICorDebug はデバッガと対象のビット数が一致していないと繋がらないので、
    /// 64bit 版と 32bit 版（<c>*.x86.exe</c>）の2本を置き、対象のビット数で選ぶ。</summary>
    public static string NetFxExecutable(bool is32Bit)
    {
        var configured = Environment.GetEnvironmentVariable(NetFxDirectoryVariable);
        var directory = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "netfxdbg");
        return Path.Combine(directory, is32Bit ? "sk0ya.Loomo.NetFxDebug.x86.exe" : "sk0ya.Loomo.NetFxDebug.exe");
    }

    public static readonly IReadOnlyList<DebugAdapterInfo> Adapters = new[] { Netcoredbg, JsDebug };

    /// <summary>その拡張子に対応するアダプタを返す。無ければ null。</summary>
    public static DebugAdapterInfo? ByExtension(string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return Adapters.FirstOrDefault(
            a => a.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)));
    }
}
