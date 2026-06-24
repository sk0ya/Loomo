using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Editor.Core.Lsp;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.Services.Lsp;

/// <summary>設定UIに出す言語サーバー1行。エディタの登録（拡張子→実行ファイル）に、カタログの表示名／
/// インストールコマンドと、実行ファイルが PATH 上に在るか（=導入済みか）を重ねたもの。</summary>
public sealed record LspServerRow(
    string Extension,
    string DisplayName,
    string Executable,
    string[] Args,
    string LanguageId,
    bool Installed,
    LspServerOrigin Origin,
    string? InstallCommand,
    string? DocsUrl);

/// <summary>ファイルを開いたときに出す「インストールを促す」内容。</summary>
public enum LspPromptKind
{
    /// <summary>対応サーバーは判っているが PATH 上に見つからない（インストールを促す）。</summary>
    NotInstalled,
    /// <summary>この拡張子に対応する言語サーバーが未設定（設定で追加するよう促す）。</summary>
    NotConfigured,
}

/// <summary>ファイルオープン時の促しバー1件分の情報。</summary>
public sealed record LspPromptInfo(
    string Extension,
    LspPromptKind Kind,
    string Message,
    string? InstallCommand,
    string? DisplayName,
    string? DocsUrl);

/// <summary>
/// Loomo 側の LSP 管理サービス。エディタの <see cref="LspServerRegistry"/>（拡張子→実行ファイルの
/// 対応表・永続化はホスト指定の場所＝Loomo 配下）を土台に、(1) 各サーバーが PATH 上に導入済みかを検出、
/// (2) 見えるターミナルでのインストール実行、(3) 追加/削除/既定復帰、(4) ファイルオープン時の促し判定を担う。
/// 「どの実行ファイルか」はエディタが、「どう入れるか・どう見せるか」は Loomo が持つ、という分担。
/// </summary>
public sealed class LspManagementService
{
    private readonly ITerminalService _terminal;
    private readonly LspServerRegistry _registry;

    public LspManagementService(ITerminalService terminal)
        : this(terminal, LspServerRegistry.Default) { }

    // テスト用に明示注入できるオーバーロード。
    internal LspManagementService(ITerminalService terminal, LspServerRegistry registry)
    {
        _terminal = terminal;
        _registry = registry;
    }

    /// <summary>現在の対応表を、表示名・インストールコマンド・導入状況つきの行に変換して返す（拡張子順）。</summary>
    public IReadOnlyList<LspServerRow> GetRows() =>
        _registry.List().Select(ToRow).ToList();

    private LspServerRow ToRow(LspServerEntry e)
    {
        var info = LspServerCatalog.ByExecutable(e.Server.Executable);
        return new LspServerRow(
            e.Extension,
            info?.DisplayName ?? e.Server.Executable,
            e.Server.Executable,
            e.Server.Args,
            e.Server.LanguageId,
            ExecutableResolver.IsOnPath(e.Server.Executable),
            e.Origin,
            info?.InstallCommand,
            info?.DocsUrl);
    }

    /// <summary>実行ファイルが PATH 上に在る（=導入済み）か。</summary>
    public bool IsInstalled(string executable) => ExecutableResolver.IsOnPath(executable);

    /// <summary>拡張子にサーバーを割り当て（または置換）して永続化する。</summary>
    public void AddOrUpdate(string extension, string executable, string[] args, string? languageId = null)
    {
        var ext = LspServerRegistry.NormalizeExt(extension);
        var langId = string.IsNullOrWhiteSpace(languageId) ? ext.TrimStart('.') : languageId!;
        _registry.Set(ext, new LspServerDef(executable, args ?? [], langId));
    }

    /// <summary>カスタムは削除、組み込みは無効化（再起動後も保持）。</summary>
    public bool Remove(string extension) => _registry.Remove(extension);

    /// <summary>ユーザー変更を捨てて組み込み既定へ戻す。</summary>
    public bool Reset(string extension) => _registry.Reset(extension);

    /// <summary>インストールコマンドを見えるターミナルで実行する。端末未接続なら false。</summary>
    public bool RunInstall(string installCommand) =>
        !string.IsNullOrWhiteSpace(installCommand) && _terminal.TryRunInVisibleTerminal(installCommand);

    /// <summary>
    /// 開いたファイルに対して促しバーを出すべきか判定する（出さないときは null）。
    /// 対応サーバーが在って導入済み → null。在るが未導入 → NotInstalled。未設定の拡張子 → NotConfigured。
    /// 拡張子の無いファイルは対象外。「今後表示しない」のフィルタは呼び出し側（App）が行う。
    /// </summary>
    public LspPromptInfo? EvaluateForFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        var def = _registry.GetForExtension(ext);
        if (def is not null)
        {
            if (ExecutableResolver.IsOnPath(def.Executable))
                return null;   // 導入済み・設定済み → 何も出さない

            var info = LspServerCatalog.ByExecutable(def.Executable);
            var name = info?.DisplayName ?? def.Executable;
            return new LspPromptInfo(
                ext, LspPromptKind.NotInstalled,
                $"「{ext}」の言語サーバー {name} が見つかりません。インストールしますか？",
                info?.InstallCommand, name, info?.DocsUrl);
        }

        // 未設定の拡張子。カタログに導入候補があればそれを提示、無ければ設定で追加を促す。
        var candidate = LspServerCatalog.ByExtension(ext).FirstOrDefault();
        if (candidate is not null)
            return new LspPromptInfo(
                ext, LspPromptKind.NotInstalled,
                $"「{ext}」の言語サーバー {candidate.DisplayName} が未設定です。インストールしますか？",
                candidate.InstallCommand, candidate.DisplayName, candidate.DocsUrl);

        return new LspPromptInfo(
            ext, LspPromptKind.NotConfigured,
            $"「{ext}」に対応する言語サーバーが設定されていません。設定で追加できます。",
            null, null, null);
    }

    /// <summary>促しバーの「インストール」用。拡張子がまだ未設定ならカタログ候補を登録してから、
    /// 提示したインストールコマンドを見えるターミナルで実行する。コマンドが無ければ false。</summary>
    public bool InstallForPrompt(LspPromptInfo info)
    {
        if (info.InstallCommand is null) return false;

        var ext = LspServerRegistry.NormalizeExt(info.Extension);
        if (_registry.GetForExtension(ext) is null
            && LspServerCatalog.ByExtension(ext).FirstOrDefault() is { } candidate)
        {
            _registry.Set(ext, new LspServerDef(candidate.Executable, candidate.Args, candidate.LanguageId));
        }

        return RunInstall(info.InstallCommand);
    }
}

/// <summary>実行ファイルが PATH（＋PATHEXT）上で解決できるか調べる小さなヘルパー。
/// npm 等のグローバル導入が <c>.cmd</c> シムになる Windows でも拾えるよう PATHEXT を総当たりする。</summary>
public static class ExecutableResolver
{
    public static bool IsOnPath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;

        // パス区切りを含むなら相対/絶対パス指定として直接確認。
        if (executable.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return File.Exists(executable);

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var pathExts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in pathDirs)
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                if (Path.HasExtension(executable) && File.Exists(Path.Combine(dir, executable)))
                    return true;
                foreach (var ext in pathExts)
                    if (File.Exists(Path.Combine(dir, executable + ext)))
                        return true;
            }
            catch
            {
                // 不正な PATH 要素（無効な文字等）は読み飛ばす。
            }
        }
        return false;
    }
}
