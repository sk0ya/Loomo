using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services.Formatting;

/// <summary>
/// 既知の整形フォーマッタ1件分のメタ情報。<see cref="Executable"/> をキーに、エディタの
/// <c>FormatterRegistry</c>（拡張子→CLI の対応表）と突き合わせ、設定UIに「表示名・対象拡張子・
/// 引数・インストールコマンド・導入手順URL」を補う。<see cref="Args"/> はエディタの
/// <c>KnownFormatters</c> が PATH 自動検出時に使う引数（<c>{file}</c> プレースホルダ込み）と
/// 一致させてあり、「適用」で登録した結果が自動検出と同じになるようにしている。
/// </summary>
public sealed record FormatterInfo(
    string Executable,
    string DisplayName,
    string[] Extensions,
    string? InstallCommand,
    string[] Args,
    string? DocsUrl = null);

/// <summary>
/// Loomo が知っている整形フォーマッタのカタログ。エディタが PATH 自動検出する CLI 群と同じ
/// 実行ファイル・引数・対象拡張子を並べ、各フォーマッタの**インストールコマンド**（Loomo から
/// 見えるターミナルで実行する用）と導入手順URLを足す。「どの CLI をどの拡張子に割り当てるか」は
/// エディタ側 <c>FormatterRegistry</c> が所有するが、「どうやって入れるか」はアプリ＝Loomo の
/// 関心なのでここに置く（言語サーバーの <c>LspServerCatalog</c> と対をなす）。
/// </summary>
public static class FormatterCatalog
{
    /// <summary>winget / npm / pip 等、Windows 向けを優先したベストエフォートのインストール
    /// コマンド付きカタログ。引数はエディタの <c>KnownFormatters</c> と一致させる。</summary>
    public static readonly IReadOnlyList<FormatterInfo> Formatters = new[]
    {
        new FormatterInfo("prettier", "Prettier",
            [".js", ".jsx", ".ts", ".tsx", ".json", ".css", ".scss", ".less", ".html", ".md", ".markdown", ".yaml", ".yml"],
            "npm install -g prettier", ["--stdin-filepath", "{file}"],
            "https://prettier.io/"),
        new FormatterInfo("dprint", "dprint",
            [".js", ".ts", ".json", ".md", ".markdown", ".toml"],
            "winget install --id dprint.dprint -e", ["fmt", "--stdin", "{file}"],
            "https://dprint.dev/"),
        new FormatterInfo("black", "Black (Python)", [".py"],
            "pip install black", ["-q", "-"],
            "https://black.readthedocs.io/"),
        new FormatterInfo("ruff", "Ruff (Python)", [".py"],
            "pip install ruff", ["format", "-"],
            "https://docs.astral.sh/ruff/"),
        new FormatterInfo("rustfmt", "rustfmt (Rust)", [".rs"],
            "rustup component add rustfmt", [],
            "https://github.com/rust-lang/rustfmt"),
        new FormatterInfo("gofmt", "gofmt (Go)", [".go"],
            "winget install --id GoLang.Go -e", [],
            "https://pkg.go.dev/cmd/gofmt"),
        new FormatterInfo("stylua", "StyLua (Lua)", [".lua"],
            "winget install --id JohnnyMorganz.StyLua -e", ["-"],
            "https://github.com/JohnnyMorganz/StyLua"),
    };

    /// <summary>実行ファイル名（拡張子の有無は無視）でカタログ項目を引く。無ければ null。</summary>
    public static FormatterInfo? ByExecutable(string executable)
    {
        var name = NormalizeExe(executable);
        return Formatters.FirstOrDefault(f => NormalizeExe(f.Executable) == name);
    }

    /// <summary>その拡張子に対応するカタログ項目（インストール候補）を返す。無ければ空。</summary>
    public static IReadOnlyList<FormatterInfo> ByExtension(string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return Formatters
            .Where(f => f.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
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
