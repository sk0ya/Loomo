using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>プロジェクト／ソリューション範囲のコード整理の結果。<see cref="Edit"/> は全ファイルぶんを 1 つにまとめた
/// WorkspaceEdit（適用は既存のトランザクション＝全件検証してから適用・失敗で巻き戻し・Undo 1 回）。</summary>
public sealed record CSharpBulkCleanupResult(
    LspWorkspaceEdit? Edit,
    IReadOnlyDictionary<string, string> ExpectedTexts,
    int FilesScanned,
    int FilesChanged,
    int ChangedLines,
    int GeneratedSkipped,
    IReadOnlyList<string> Failures);

/// <summary>複数ファイルのコード整理。ファイルごとに <see cref="CSharpCleanupService"/> を当て、変わったものだけを集める。
///
/// <para>意味解析（未使用 using の削除）は使わない——ファイルごとにプロジェクトの Compilation を作ると
/// ソリューション全体では現実的な時間に収まらない。未使用 using の一括削除は Fix All（IDE0005）の仕事。</para>
/// </summary>
public static class CSharpBulkCleanup
{
    public static CSharpBulkCleanupResult Run(
        IReadOnlyList<string> files,
        Func<string, string?> readText,
        CSharpCleanupProfile profile,
        CSharpEditorConfigService? editorConfig = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(readText);
        editorConfig ??= new CSharpEditorConfigService();

        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.OrdinalIgnoreCase);
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var generated = 0;
        var changedLines = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(file);
            if (readText(path) is not { } text)
            {
                failures.Add($"{Path.GetFileName(path)}: 読み取れません");
                continue;
            }

            var options = CSharpCleanupScope.OptionsFor(profile,
                CSharpCleanupOptionsFactory.CreateForFile(path, editorConfigService: editorConfig));
            var result = CSharpCleanupService.Clean(path, text, options);
            if (result.IsGeneratedCode)
            {
                generated++;
                continue;
            }
            if (result.Edit is not { } edit) continue;
            foreach (var (uri, edits) in edit.Changes)
                changes[uri] = edits;
            expected[path] = text;
            changedLines += result.ChangedLines;
        }

        return new CSharpBulkCleanupResult(
            changes.Count == 0 ? null : new LspWorkspaceEdit(changes) { ExpectedTexts = expected },
            expected, files.Count, changes.Count, changedLines, generated, failures);
    }
}
