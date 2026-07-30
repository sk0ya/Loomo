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
/// Loomo 側の LSP 管理サービス。<see cref="LspServerTable"/>（拡張子→実行ファイルの対応表）の上に、
/// (1) 各サーバーが PATH 上に導入済みかの検出、(2) 見えるターミナルでのインストール実行、
/// (3) 追加/削除/既定復帰、(4) ファイルオープン時の促し判定を重ねる。
///
/// <para>表は**必ず注入**する。以前あった「注入が無ければ既定の表を作る」コンストラクタは、
/// 設定画面とエディタが別インスタンスを見る分裂の原因になっていたので削除した（設計書 §30.2.1）。</para>
/// </summary>
public sealed class LspManagementService
{
    private readonly ITerminalService _terminal;
    private readonly LspServerTable _registry;

    public LspManagementService(ITerminalService terminal, LspServerTable registry)
    {
        _terminal = terminal;
        _registry = registry;
    }

    /// <summary>現在の対応表を、表示名・インストールコマンド・導入状況つきの行に変換して返す（拡張子順）。</summary>
    public IReadOnlyList<LspServerRow> GetRows() =>
        _registry.List().Select(ToRow).ToList();

    // Roslyn を「Custom だが BuiltIn として見せる」補正はもう要らない（組み込み表がカタログ由来に
    // なったので Roslyn は本当に組み込み）。残すと無効化した .cs が BuiltIn に見えてしまう。
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
        var ext = LspExtensions.NormalizeExt(extension);
        var langId = string.IsNullOrWhiteSpace(languageId) ? ext.TrimStart('.') : languageId!;
        _registry.Set(ext, new LspServerDef(executable, args ?? [], langId));
    }

    /// <summary>カスタムは削除、組み込みは無効化（再起動後も保持）。</summary>
    public bool Remove(string extension) => _registry.Remove(extension);

    /// <summary>ユーザー変更を捨てて組み込み既定へ戻す。</summary>
    /// <remarks>.cs を Roslyn へ明示的に付け替える特例は不要になった（組み込みが Roslyn なので、
    /// 素の Reset が Roslyn を復元する）。</remarks>
    public bool Reset(string extension) => _registry.Reset(extension);

    /// <summary>インストールコマンドを見えるターミナルで実行する。端末未接続なら false。</summary>
    public bool RunInstall(string installCommand) =>
        !string.IsNullOrWhiteSpace(installCommand) && _terminal.TryRunInVisibleTerminal(installCommand);

    /// <summary>
    /// 「言語サーバーが設定されていません」（<see cref="LspPromptKind.NotConfigured"/>）を出してよい拡張子。
    /// カタログにも対応表にも無い拡張子で無条件に促すと、<c>.png</c> / <c>.zip</c> のように**そもそも言語
    /// サーバーが存在しえない**ファイルにまで案内が出て邪魔になるため、プログラミング言語のソースだけに絞る
    /// （カタログにある拡張子は手前の分岐で処理されるので、ここは「LSP はあるが Loomo のカタログに無い言語」
    /// を拾うための表）。文書・設定・データ形式（.md/.json/.xml/.csv/.yaml …）は Loomo 側に専用の
    /// EditorSupport 提供者があり LSP 無しでも困らないので、あえて含めない。
    /// </summary>
    private static readonly HashSet<string> PromptableSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx", ".m", ".mm",
        ".cs", ".csx", ".fs", ".fsx", ".vb",
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".vue", ".svelte",
        ".py", ".pyi", ".rb", ".php", ".go", ".rs", ".java", ".kt", ".kts", ".scala", ".groovy",
        ".swift", ".dart", ".zig", ".hs", ".ex", ".exs", ".erl", ".lua", ".pl", ".pm", ".r", ".jl",
        ".sql", ".sh", ".bash", ".zsh", ".ps1", ".psm1",
        ".css", ".scss", ".sass", ".less",
    };

    /// <summary>この拡張子に言語サーバーが在りうるか（未設定の促しを出してよいか）。</summary>
    public static bool CanHaveLanguageServer(string extension) =>
        PromptableSourceExtensions.Contains(LspExtensions.NormalizeExt(extension));

    /// <summary>
    /// 開いたファイルに対して促しバーを出すべきか判定する（出さないときは null）。
    /// 対応サーバーが在って導入済み → null。在るが未導入 → NotInstalled。
    /// 未設定の拡張子は、ソースコードとみなせるものだけ NotConfigured（それ以外は null）。
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

        // 画像・書庫・バイナリ等、言語サーバーが存在しえない拡張子では何も出さない。
        if (!CanHaveLanguageServer(ext))
            return null;

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

        var ext = LspExtensions.NormalizeExt(info.Extension);
        if (_registry.GetForExtension(ext) is null
            && LspServerCatalog.ByExtension(ext).FirstOrDefault() is { } candidate)
        {
            _registry.Set(ext, new LspServerDef(candidate.Executable, candidate.Args, candidate.LanguageIdFor(ext)));
        }

        return RunInstall(info.InstallCommand);
    }
}

/// <summary>実行ファイルが PATH（＋PATHEXT）上で解決できるか調べる小さなヘルパー。
/// npm 等のグローバル導入が <c>.cmd</c> シムになる Windows でも拾えるよう PATHEXT を総当たりする。
///
/// PATH はプロセス起動時のスナップショットだけでなく、**レジストリの最新 Machine/User PATH** も
/// 読み直して統合する。インストーラ（dotnet tool / npm -g / winget …）が新たに足したディレクトリ
/// （例: <c>%USERPROFILE%\.dotnet\tools</c>）を、Loomo を再起動せずに「導入済み」と認識できるようにするため。</summary>
public static class ExecutableResolver
{
    public static bool IsOnPath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;

        // パス区切りを含むなら相対/絶対パス指定として直接確認。
        if (executable.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return File.Exists(executable);

        var pathExts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in EnumeratePathDirs())
        {
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

    /// <summary>プロセス PATH ＋ レジストリの最新 Machine/User PATH を統合し、重複を除いて返す。
    /// レジストリ読み（Machine/User）は env ブロックのスナップショットではなく現在値を返すので、
    /// インストール直後の新規ディレクトリも拾える。</summary>
    private static IEnumerable<string> EnumeratePathDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new[]
        {
            EnvironmentVariableTarget.Process,
            EnvironmentVariableTarget.Machine,
            EnvironmentVariableTarget.User,
        };

        foreach (var target in targets)
        {
            string? raw;
            try { raw = Environment.GetEnvironmentVariable("PATH", target); }
            catch { raw = null; }   // レジストリ読込が失敗してもプロセス PATH で続行
            if (string.IsNullOrEmpty(raw)) continue;

            foreach (var part in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = part.Trim().Trim('"');
                if (dir.Length == 0) continue;
                // 末尾の区切りを正規化して重複判定の取りこぼしを減らす。
                var key = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (seen.Add(key.Length == 0 ? dir : key))
                    yield return dir;
            }
        }
    }
}
