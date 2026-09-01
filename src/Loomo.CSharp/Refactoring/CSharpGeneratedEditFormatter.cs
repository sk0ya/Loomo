using Editor.Core.Lsp;
using Microsoft.CodeAnalysis.Text;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>C#固有操作が作ったWorkspaceEditを、適用前に現在文書の設定で整形するアダプター。
/// 編集を作る各リファクタリングへindent実装を重複させず、生成コードだけを整形する。</summary>
public static class CSharpGeneratedEditFormatter
{
    /// <summary>WorkspaceEditの各C#文書を、文書ごとの.editorconfigで整形する。
    /// 新規作成ファイルは<paramref name="originalTexts" />に空本文を渡せば対象になる。
    /// ファイルごとの整形を編集生成側へ重複させず、作成を含む複数文書操作を一つのEditとして返す。</summary>
    public static LspWorkspaceEdit FormatWorkspace(
        LspWorkspaceEdit edit,
        IReadOnlyDictionary<string, string> originalTexts,
        Func<string, CSharpCleanupOptions> optionsForPath)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(originalTexts);
        ArgumentNullException.ThrowIfNull(optionsForPath);

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawPath, text) in originalTexts)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            try { normalized[Path.GetFullPath(rawPath)] = text; }
            catch (ArgumentException) { }
        }
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            edit.Changes, StringComparer.OrdinalIgnoreCase);
        foreach (var (uri, edits) in edit.Changes)
        {
            if (edits.Count == 0 || LspUri.TryToLocalPath(uri) is not { } rawPath)
                continue;
            var path = Path.GetFullPath(rawPath);
            if (!normalized.TryGetValue(path, out var originalText))
                continue;

            var updated = ApplyTextEdits(originalText, edits);
            var options = optionsForPath(path) with
            {
                OrganizeUsings = false,
                TrimTrailingWhitespace = false,
                EndOfLine = null,
                InsertFinalNewline = null,
                ExcludeGeneratedCode = false,
            };
            var formatted = CSharpCleanupService.Clean(path, updated, options);
            var replacement = formatted.Edit?.Changes.TryGetValue(
                LspUri.FromPath(path), out var formattedEdits) == true &&
                formattedEdits.Count == 1
                ? formattedEdits[0].NewText
                : updated;
            changes[uri] = string.Equals(replacement, originalText, StringComparison.Ordinal)
                ? edits
                : [new LspTextEdit(FullDocumentRange(originalText), replacement)];
        }

        return edit with { Changes = changes };
    }

    public static LspWorkspaceEdit FormatCurrentFile(
        string filePath,
        string originalText,
        LspWorkspaceEdit edit,
        CSharpCleanupOptions options)
        => FormatWorkspace(edit,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(filePath)] = originalText,
            },
            _ => options);

    private static string ApplyTextEdits(string sourceText, IReadOnlyList<LspTextEdit> edits)
    {
        var source = SourceText.From(sourceText);
        var changes = edits.Select(edit => new TextChange(
                ToTextSpan(source, edit.Range), edit.NewText))
            .OrderByDescending(change => change.Span.Start)
            .ToArray();
        return changes.Length == 0 ? sourceText : source.WithChanges(changes).ToString();
    }

    private static TextSpan ToTextSpan(SourceText source, LspRange range)
        => TextSpan.FromBounds(
            source.Lines.GetPosition(new LinePosition(range.Start.Line, range.Start.Character)),
            source.Lines.GetPosition(new LinePosition(range.End.Line, range.End.Character)));

    private static LspRange FullDocumentRange(string text)
    {
        var source = SourceText.From(text);
        var end = source.Lines.GetLinePosition(source.Length);
        return new LspRange(new LspPosition(0, 0), new LspPosition(end.Line, end.Character));
    }
}
