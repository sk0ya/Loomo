using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Formatting;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Services.Lsp;   // ExecutableResolver（PATH 検出ヘルパーを共用）

namespace sk0ya.Loomo.Services.Formatting;

/// <summary>設定UIに出す整形フォーマッタ1行。カタログ由来の「既知フォーマッタ」行と、ユーザーが
/// 手動登録した「カスタム」行の両方をこの1型で表す（<see cref="IsCustom"/> で区別）。</summary>
public sealed record FormatterRow(
    string Key,                 // カタログ行は実行ファイル名、カスタム行は拡張子
    string DisplayName,
    string Executable,
    string[] Args,
    IReadOnlyList<string> Extensions,
    string ExtensionsLabel,
    bool Installed,             // 実行ファイルが PATH 上にあるか
    bool Configured,            // いずれかの拡張子で現在このフォーマッタが選ばれているか
    bool IsCustom,
    string? InstallCommand,
    string? DocsUrl);

/// <summary>
/// Loomo 側の整形フォーマッタ管理サービス。エディタの <see cref="FormatterRegistry"/>（拡張子→CLI の
/// 対応表・永続化はホスト指定の場所＝Loomo 配下 <c>formatters.json</c>）を土台に、(1) カタログの既知
/// フォーマッタが PATH 上に導入済みかを検出、(2) 見えるターミナルでのインストール実行、
/// (3) 拡張子への割り当て（適用）/解除、(4) 任意の拡張子→CLI のカスタム追加/削除を担う。
/// 言語サーバーの <see cref="LspManagementService"/> と対をなす（フォーマッタには組み込み既定が
/// 無いので「無効化/既定復帰」は持たず、代わりにカタログを一覧の土台にする）。
/// </summary>
public sealed class FormatterManagementService
{
    private readonly ITerminalService _terminal;
    private readonly FormatterRegistry _registry;

    public FormatterManagementService(ITerminalService terminal)
        : this(terminal, FormatterRegistry.Default) { }

    // テスト用に明示注入できるオーバーロード。
    internal FormatterManagementService(ITerminalService terminal, FormatterRegistry registry)
    {
        _terminal = terminal;
        _registry = registry;
    }

    /// <summary>
    /// 一覧行を作る。まずカタログの既知フォーマッタを（導入状況＋適用状況つきで）並べ、続けて
    /// カタログに無いユーザー登録（カスタム）を拡張子ごとに並べる。
    /// </summary>
    public IReadOnlyList<FormatterRow> GetRows()
    {
        var entries = _registry.List();
        var rows = new List<FormatterRow>();

        // 1) カタログの既知フォーマッタ。
        foreach (var f in FormatterCatalog.Formatters)
        {
            var configured = f.Extensions.Any(ext => IsAssignedTo(entries, ext, f.Executable));
            rows.Add(new FormatterRow(
                Key: f.Executable,
                DisplayName: f.DisplayName,
                Executable: f.Executable,
                Args: f.Args,
                Extensions: f.Extensions,
                ExtensionsLabel: string.Join(" ", f.Extensions),
                Installed: ExecutableResolver.IsOnPath(f.Executable),
                Configured: configured,
                IsCustom: false,
                InstallCommand: f.InstallCommand,
                DocsUrl: f.DocsUrl));
        }

        // 2) カタログに無いカスタム登録（拡張子ごと）。
        foreach (var e in entries)
        {
            if (FormatterCatalog.ByExecutable(e.Def.Executable) is not null) continue;
            rows.Add(new FormatterRow(
                Key: e.Extension,
                DisplayName: e.Def.Executable,
                Executable: e.Def.Executable,
                Args: e.Def.Args,
                Extensions: [e.Extension],
                ExtensionsLabel: e.Extension,
                Installed: ExecutableResolver.IsOnPath(e.Def.Executable),
                Configured: true,
                IsCustom: true,
                InstallCommand: null,
                DocsUrl: null));
        }

        return rows;
    }

    /// <summary>実行ファイルが PATH 上に在る（=導入済み）か。</summary>
    public bool IsInstalled(string executable) => ExecutableResolver.IsOnPath(executable);

    /// <summary>インストールコマンドを見えるターミナルで実行する。端末未接続なら false。</summary>
    public bool RunInstall(string? installCommand) =>
        !string.IsNullOrWhiteSpace(installCommand) && _terminal.TryRunInVisibleTerminal(installCommand!);

    /// <summary>カタログのフォーマッタを、その対象拡張子すべてに割り当てる（既存の割り当ては上書き）。</summary>
    public void Apply(FormatterInfo info)
    {
        foreach (var ext in info.Extensions)
            _registry.Set(FormatterRegistry.NormalizeExt(ext), new FormatterDef(info.Executable, info.Args));
    }

    /// <summary>カタログのフォーマッタを、それが割り当たっている拡張子から外す（他CLIの割り当ては触らない）。</summary>
    public void Unapply(FormatterInfo info)
    {
        var entries = _registry.List();
        foreach (var ext in info.Extensions)
            if (IsAssignedTo(entries, ext, info.Executable))
                _registry.Remove(FormatterRegistry.NormalizeExt(ext));
    }

    /// <summary>拡張子にフォーマッタを割り当て（または置換）して永続化する（カスタム追加）。</summary>
    public void AddOrUpdate(string extension, string executable, string[] args)
    {
        var ext = FormatterRegistry.NormalizeExt(extension);
        _registry.Set(ext, new FormatterDef(executable, args ?? []));
    }

    /// <summary>拡張子の割り当てを削除する。</summary>
    public bool Remove(string extension) => _registry.Remove(extension);

    private static bool IsAssignedTo(IReadOnlyList<FormatterEntry> entries, string ext, string executable)
    {
        var norm = FormatterRegistry.NormalizeExt(ext);
        return entries.Any(e =>
            string.Equals(e.Extension, norm, StringComparison.OrdinalIgnoreCase) &&
            SameExe(e.Def.Executable, executable));
    }

    private static bool SameExe(string a, string b) =>
        string.Equals(Strip(a), Strip(b), StringComparison.OrdinalIgnoreCase);

    private static string Strip(string exe)
    {
        var name = exe.Trim();
        foreach (var suffix in new[] { ".exe", ".cmd", ".bat", ".ps1" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length];
        return name;
    }
}
