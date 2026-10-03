using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Editor.Controls;
using Editor.Controls.Formatting;
using Editor.Core.Formatting;
using Editor.Core.Lsp;
using sk0ya.Loomo.Core.Settings;

namespace sk0ya.Loomo.Services.Formatting;

/// <summary>
/// 保存時に何を走らせるかの判定（純関数）。<see cref="EditorSettings.CleanCSharpOnSave"/> が ON の
/// <c>.cs</c> は C# cleanup が整形・行末空白・末尾改行まで .editorconfig に沿って済ませるので、
/// 汎用の保存時整形と .editorconfig の空白規則は<b>走らせない</b>（同じ本文を二度整えて undo が
/// 2 段になる・C# の文字列リテラル内の行末空白を守る cleanup の判断を汎用処理が上書きする、を避ける）。
/// </summary>
public static class FormatOnSavePolicy
{
    /// <summary>この保存を C# cleanup が引き受けるか。</summary>
    public static bool IsHandledByCSharpCleanup(string path, EditorSettings settings)
        => settings.CleanCSharpOnSave &&
           string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>汎用の保存時整形（<c>:Format</c> と同じ経路）を走らせるか。</summary>
    public static bool ShouldFormat(string path, EditorSettings settings)
    {
        if (!settings.FormatOnSave || IsHandledByCSharpCleanup(path, settings)) return false;
        var ext = FormatterRegistry.NormalizeExt(Path.GetExtension(path));
        if (ext.Length == 0) return false;
        return !settings.FormatOnSaveExcludedExtensions.Any(excluded =>
            string.Equals(FormatterRegistry.NormalizeExt(excluded), ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>.editorconfig の空白規則を保存時に適用するか。</summary>
    public static bool ShouldApplyEditorConfig(string path, EditorSettings settings)
        => settings.ApplyEditorConfigOnSave && !IsHandledByCSharpCleanup(path, settings);

    /// <summary>設定画面の入力（空白・カンマ・セミコロン区切り。<c>md</c> も <c>.md</c> も可）を
    /// 正規化した拡張子一覧へ直す。重複は落とし、入力順を保つ。</summary>
    public static List<string> ParseExtensions(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Split([' ', '\t', ',', ';', '、'], StringSplitOptions.RemoveEmptyEntries))
        {
            var ext = FormatterRegistry.NormalizeExt(raw.Trim());
            if (ext.Length > 1 && !result.Contains(ext, StringComparer.OrdinalIgnoreCase))
                result.Add(ext);
        }
        return result;
    }

    /// <summary>設定画面へ戻す表示形（空白区切り）。</summary>
    public static string FormatExtensions(IEnumerable<string> extensions) => string.Join(' ', extensions);
}

/// <summary>
/// .editorconfig のうち保存時に効く空白規則。<b>null は「書かれていない」</b>で、そのときは何もしない
/// （VS Code と同じく、既定で行末空白を消したり末尾改行を足したりはしない）。
/// </summary>
/// <param name="TrimTrailingWhitespace"><c>trim_trailing_whitespace</c>。</param>
/// <param name="InsertFinalNewline"><c>insert_final_newline</c>。<c>false</c> は「足さない」だけで、
/// 既にある末尾改行を削りはしない（VS Code の EditorConfig 拡張と同じ。黙って本文を削らない）。</param>
/// <param name="FileFormat"><c>end_of_line</c> を Vim の <c>fileformat</c>（unix/dos/mac）へ写したもの。</param>
public sealed record EditorConfigWhitespaceRules(
    bool? TrimTrailingWhitespace, bool? InsertFinalNewline, string? FileFormat)
{
    public static readonly EditorConfigWhitespaceRules None = new(null, null, null);

    public bool IsEmpty => TrimTrailingWhitespace is null && InsertFinalNewline is null && FileFormat is null;

    /// <summary>解決済みの .editorconfig プロパティ（キーは小文字）から規則を読む。</summary>
    public static EditorConfigWhitespaceRules From(Func<string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        return new(
            ParseBool(get("trim_trailing_whitespace")),
            ParseBool(get("insert_final_newline")),
            get("end_of_line")?.Trim().ToLowerInvariant() switch
            {
                "lf" => "unix",
                "crlf" => "dos",
                "cr" => "mac",
                _ => null,
            });
    }

    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,   // unset・不正値は「書かれていない」扱い
    };
}

/// <summary>.editorconfig の空白規則を本文へ当てる純関数。エディタの本文は行を <c>\n</c> で
/// つないだもの（改行コードはバッファの <c>fileformat</c> が保存時に決める）なので、ここでは
/// <c>\n</c> 区切りとして扱う。<c>end_of_line</c> は本文ではなく <c>fileformat</c> で当てる。</summary>
public static class EditorConfigWhitespace
{
    public static string Apply(string text, EditorConfigWhitespaceRules rules)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rules);
        var result = text;
        if (rules.TrimTrailingWhitespace == true)
            result = TrimTrailingWhitespace(result);
        // 空のファイルへは足さない（改行1つだけのファイルを作らない。VS Code と同じ）。
        if (rules.InsertFinalNewline == true && result.Length > 0 && !result.EndsWith('\n'))
            result += "\n";
        return result;
    }

    /// <summary>各行末の半角スペースとタブだけを削る。全角スペースは本文の一部として残す。</summary>
    private static string TrimTrailingWhitespace(string text)
    {
        var lines = text.Split('\n');
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd(' ', '\t', '\r');
            if (trimmed.Length == line.Length) continue;
            lines[i] = trimmed;
            changed = true;
        }
        return changed ? string.Join('\n', lines) : text;
    }
}

/// <summary>保存時整形の結果。<see cref="Text"/> が null なら本文は変えない。
/// <see cref="Error"/> は利用者へ見せる失敗理由（保存自体は続ける）。</summary>
public sealed record SaveFormatResult(string? Text, string? FormatterName, string? Error)
{
    public static readonly SaveFormatResult Unchanged = new(null, null, null);
    public static SaveFormatResult Failed(string error) => new(null, null, error);
}

/// <summary>
/// 保存時整形の本体。経路はエディタの <c>:Format</c>（<c>HandleFormatDocumentAsync</c>）と同じ順：
/// ① その拡張子に設定済みの CLI 整形 → ② 言語サーバーの <c>textDocument/formatting</c> →
/// ③ PATH 上の既知整形ツール（見つけたら <c>:Format</c> と同じく対応表へ登録する）。
/// <para><c>:Format</c> の実装はコントロールの private なので、公開部品（<see cref="FormatterRegistry"/>・
/// <see cref="FormatterRunner"/>・<see cref="KnownFormatters"/>・<see cref="ILspDocument"/>）から同じ順を組み直す。
/// 違いは保存向けに<b>時間を区切る</b>ことだけ——保存は待たせても止めてはいけない。</para>
/// </summary>
public sealed class SaveFormatter
{
    /// <summary>保存時整形の既定の待ち時間。CLI の初回起動（npx/prettier 等）を見込みつつ、保存を長く止めない。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<FormatterDef, string?, string, int, FormatterRunner.RunResult> _run;
    private readonly Func<string, bool> _isOnPath;

    public SaveFormatter()
        : this((def, path, input, timeoutMs) => FormatterRunner.Run(def, path, input, timeoutMs),
               FormatterRunner.IsOnPath)
    {
    }

    /// <summary>テスト用：CLI の実行と PATH 判定を差し替える。</summary>
    public SaveFormatter(
        Func<FormatterDef, string?, string, int, FormatterRunner.RunResult> run,
        Func<string, bool> isOnPath)
    {
        _run = run;
        _isOnPath = isOnPath;
    }

    /// <summary>本文 <paramref name="text"/>（<c>\n</c> 区切り）を整形した結果を返す。例外は投げない。</summary>
    /// <param name="lsp">保存先と同じ文書の LSP ハンドル。「名前を付けて保存」で別の宛先なら null を渡す。</param>
    public async Task<SaveFormatResult> FormatAsync(
        string path, string text, FormatterRegistry registry, ILspDocument? lsp,
        int tabSize, bool insertSpaces, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var ext = FormatterRegistry.NormalizeExt(Path.GetExtension(path));
        if (ext.Length == 0) return SaveFormatResult.Unchanged;
        try
        {
            // ① 設定済みの CLI 整形が LSP より優先（:Format と同じ）。
            if (registry.GetForExtension(ext) is { } configured)
                return await RunCliAsync(configured, path, text, limit);

            // ② 言語サーバーの整形。繋がっていない・応答が空なら次へ。
            if (lsp is { IsConnected: true, IsReady: true })
            {
                var request = lsp.RequestFormattingAsync(tabSize, insertSpaces);
                var finished = await Task.WhenAny(request, Task.Delay(limit));
                if (finished != request)
                {
                    ObserveLater(request);
                    return SaveFormatResult.Failed($"言語サーバーの整形が {limit.TotalSeconds:0} 秒で終わりませんでした");
                }
                var edits = await request;
                if (edits.Count > 0)
                    return Changed(text, VimEditorControl.ApplyTextEdits(text, edits), "言語サーバー");
            }

            // ③ PATH 上の既知整形ツール。PATH の探索はディスクを見るので UI スレッドから外す。
            var candidates = KnownFormatters.ForExtension(ext);
            if (candidates.Count == 0) return SaveFormatResult.Unchanged;
            var installed = await Task.Run(() => candidates.FirstOrDefault(c => _isOnPath(c.Executable)));
            if (installed is null) return SaveFormatResult.Unchanged;
            var def = new FormatterDef(installed.Executable, installed.Args);
            var result = await RunCliAsync(def, path, text, limit);
            // :Format と同じく、使えた候補はその拡張子へ登録する（次回から ① で走る・設定画面にも出る）。
            if (result.Error is null)
                registry.Set(ext, def);
            return result;
        }
        catch (Exception ex)
        {
            return SaveFormatResult.Failed(ex.Message);
        }
    }

    private async Task<SaveFormatResult> RunCliAsync(FormatterDef def, string path, string text, TimeSpan limit)
    {
        var timeoutMs = (int)Math.Clamp(limit.TotalMilliseconds, 1, int.MaxValue);
        var result = await Task.Run(() => _run(def, path, text, timeoutMs));
        if (!result.Ok)
            return SaveFormatResult.Failed($"{def.Executable}: {FirstLine(result.Error)}");
        return Changed(text, result.Output ?? "", def.Executable);
    }

    /// <summary>改行コードをバッファの表現（<c>\n</c>）へ揃えてから比べる。CRLF で返す整形ツールでも
    /// 「全行が変わった」にしない。</summary>
    private static SaveFormatResult Changed(string original, string formatted, string formatterName)
    {
        var normalized = NormalizeNewlines(formatted);
        return string.Equals(normalized, original, StringComparison.Ordinal)
            ? new SaveFormatResult(null, formatterName, null)
            : new SaveFormatResult(normalized, formatterName, null);
    }

    internal static string NormalizeNewlines(string text)
        => text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "失敗しました";
        var trimmed = message.Trim();
        var nl = trimmed.IndexOf('\n');
        return nl < 0 ? trimmed : trimmed[..nl].TrimEnd('\r') + " …";
    }

    /// <summary>見切った要求の例外を未観測のまま捨てない。</summary>
    private static void ObserveLater(Task task)
        => _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
