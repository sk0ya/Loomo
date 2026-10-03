using sk0ya.Loomo.Services.Formatting;

namespace sk0ya.Loomo.App.Views;

public partial class ShellWindow
{
    /// <summary>保存時整形の本体（<c>:Format</c> と同じ経路）。状態を持たないので 1 つを使い回す。</summary>
    private readonly SaveFormatter _saveFormatter = new();

    /// <summary>
    /// 保存前フック（<c>EditorService.BeforeSaveAsync</c>）。保存のときだけ走り、毎キーの経路には何も足さない。
    /// <list type="bullet">
    /// <item><c>.cs</c> で C# cleanup が ON なら、それだけを走らせる（整形・行末空白・末尾改行まで cleanup が
    /// .editorconfig に沿って済ませる。汎用処理を重ねると二重整形になる）。</item>
    /// <item>それ以外は「全言語の保存時整形」→「.editorconfig の空白規則」の順に本文へ当て、
    /// <b>まとめて 1 回</b>エディタへ適用する（undo 1 回で両方戻る。キャレットは同じ行・列に残る）。</item>
    /// </list>
    /// <para>ここで投げた例外は <c>EditorService</c> が受けて本文をそのまま保存する。整形の失敗・タイムアウトは
    /// 例外にせず状態行へ出すだけ——保存そのものは必ず通す。</para>
    /// </summary>
    private async Task PrepareEditorSaveAsync(VimEditorControl control, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return;
        var settings = _settings.Editor;
        // 「名前を付けて保存」で別の宛先へ書くときは、今の文書の LSP ハンドル（別 URI）を使わない。
        var sameDocument = control.FilePath is { Length: > 0 } currentPath &&
            string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase);

        if (FormatOnSavePolicy.IsHandledByCSharpCleanup(targetPath, settings))
        {
            // 保存時cleanup。単一の開いている文書だけを整え、WorkspaceEditとして記録する。
            if (sameDocument)
                await CSharpRefactoring.RunCSharpCleanupAsync(control, onSave: true);
            return;
        }

        var format = FormatOnSavePolicy.ShouldFormat(targetPath, settings);
        var applyConfig = FormatOnSavePolicy.ShouldApplyEditorConfig(targetPath, settings);
        if (!format && !applyConfig) return;

        var original = control.Text;
        // .editorconfig の探索と解析はディスクを読むので UI スレッドから外す。
        var rules = applyConfig
            ? await Task.Run(() => ResolveWhitespaceRules(targetPath))
            : EditorConfigWhitespaceRules.None;

        var text = original;
        string? error = null;
        if (format)
        {
            var options = control.Engine.Options;
            var result = await _saveFormatter.FormatAsync(
                targetPath, original, control.Engine.Services.Formatters,
                sameDocument ? control.LspDocument : null,
                options.TabStop, options.ExpandTab);
            if (result.Text is { } formatted) text = formatted;
            error = result.Error;
        }
        text = EditorConfigWhitespace.Apply(text, rules);

        if (!string.Equals(text, original, StringComparison.Ordinal))
        {
            // 整形を待つ間に打鍵されていたら、古い本文の整形結果で上書きしない（打った文字が消える）。
            if (!string.Equals(control.Text, original, StringComparison.Ordinal))
                error ??= "整形中に本文が変わったため見送りました";
            else if (!control.TryApplyLspTextEdits([WholeDocumentEdit(original, text)],
                         expectedVersion: null, out var applyError))
                error ??= applyError ?? "整形結果を適用できませんでした";
        }

        // end_of_line は本文ではなくバッファの fileformat で当たる（本文は常に \n 区切り）。
        if (rules.FileFormat is { } fileFormat &&
            !string.Equals(control.Engine.Options.FileFormat, fileFormat, StringComparison.OrdinalIgnoreCase))
            control.ExecuteCommand($"set fileformat={fileFormat}");

        if (error is not null)
            control.ShowStatusMessage($"保存時の整形に失敗しました（{error}）。本文をそのまま保存します。");
    }

    /// <summary>保存先に効く .editorconfig の空白規則。読めなければ「何も書かれていない」として扱う。</summary>
    private EditorConfigWhitespaceRules ResolveWhitespaceRules(string path)
    {
        try
        {
            var config = _csharpEditorConfig.Resolve(path);
            return EditorConfigWhitespaceRules.From(config.Get);
        }
        catch (Exception)
        {
            return EditorConfigWhitespaceRules.None;
        }
    }

    /// <summary>本文全体を置き換える 1 つの編集。エディタの本文は <c>\n</c> 区切り。</summary>
    private static Editor.Core.Lsp.LspTextEdit WholeDocumentEdit(string original, string updated)
    {
        var lastBreak = original.LastIndexOf('\n');
        var lastLine = original.Count(c => c == '\n');
        var lastLength = original.Length - (lastBreak + 1);
        return new Editor.Core.Lsp.LspTextEdit(
            new Editor.Core.Lsp.LspRange(
                new Editor.Core.Lsp.LspPosition(0, 0),
                new Editor.Core.Lsp.LspPosition(lastLine, lastLength)),
            updated);
    }
}
