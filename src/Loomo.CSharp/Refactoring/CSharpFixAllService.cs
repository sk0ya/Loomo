using Editor.Core.Lsp;
using Microsoft.CodeAnalysis.Text;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Refactoring;

/// <summary>
/// LSPがsource.fixAllを返さない場合のC# Fix All統合サービス。
/// StyleCop公式CodeFixとcompiler Quick Fixを同じ本文へ順次適用し、最終結果を元本文基準の
/// 単一WorkspaceEditへ変換する。AppはC#の診断・修正順序を知らず、結果の適用だけを行う。
/// </summary>
public static class CSharpFixAllService
{
    public static async Task<CSharpFixAllResult> ApplyAsync(
        SolutionModel solution,
        CSharpFixAllPlan plan,
        IReadOnlyDictionary<string, string>? currentTexts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsValid)
            return new(null, 0, 0, plan.Error ?? "Fix Allの対象を決められません。");

        var baseline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) continue;
            if (TryGetText(currentTexts, fullPath, out var current))
                baseline[fullPath] = current!;
            else
                baseline[fullPath] = await File.ReadAllTextAsync(fullPath, cancellationToken);
        }

        var working = new Dictionary<string, string>(baseline,
            StringComparer.OrdinalIgnoreCase);
        var actionsFound = 0;

        // compilerとStyleCopの各passは同じファイルを変更し得る。先に個別WorkspaceEditを
        // destinationへ重ねると、後のpassが前のpassを消すため、working本文だけを更新し、
        // 最後にbaselineとの差分を1つの全文編集へ変換する。
        var compiler = await CSharpCompilerCodeFixService.ApplyAllAsync(
            solution, plan.Files, working, baseline, cancellationToken);
        if (compiler.Error is { Length: > 0 } compilerError)
            return new(null, baseline.Count, actionsFound, compilerError);
        actionsFound += compiler.ActionsFound;
        if (compiler.Edit is not null)
            ApplyBaselineDocumentEdits(working, baseline, compiler.Edit.Changes);

        var styleCop = new StyleCopCodeFixService();
        // linked fileは複数projectのParseOptions／設定で異なる修正になり得る。
        // 各projectを直前のworkingへ順次適用すると、異なる結果を「合成」してしまい、
        // project境界を越えた意図しない本文を作る。全projectを同じスナップショットから
        // 評価し、URI単位で完全一致だけを統合してからworkingへ一度だけ反映する。
        var styleCopBase = new Dictionary<string, string>(working,
            StringComparer.OrdinalIgnoreCase);
        var styleCopChanges = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var project in plan.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectFiles = project.SelectedTargetFrameworkModel?.CompileFiles
                .Select(item => Path.GetFullPath(item.FullPath))
                .Where(path => plan.Files.Contains(path, StringComparer.OrdinalIgnoreCase))
                .ToArray() ?? [];
            if (styleCop.IsAvailable(project))
            {
                var result = await styleCop.ApplyAllAsync(
                    project, projectFiles, styleCopBase, cancellationToken);
                if (result.Error is { Length: > 0 } error)
                    return new(null, baseline.Count, actionsFound, error);

                actionsFound += result.ActionsFound;
                if (result.Edit is null) continue;
                if (CSharpFixAllEditMerger.Merge(styleCopChanges, result.Edit.Changes)
                    is { } mergeError)
                    return new(null, baseline.Count, actionsFound, mergeError);
            }
        }
        ApplyToWorking(working, styleCopChanges);

        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (path, original) in baseline)
        {
            if (!working.TryGetValue(path, out var updated) ||
                string.Equals(original, updated, StringComparison.Ordinal))
                continue;
            changes[LspUri.FromPath(path)] =
                [new LspTextEdit(FullDocumentRange(original), updated)];
        }

        return new(
            changes.Count == 0 ? null : CreateWorkspaceEdit(changes, baseline),
            baseline.Count, actionsFound, ExpectedTexts: baseline);
    }

    private static LspWorkspaceEdit CreateWorkspaceEdit(
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> changes,
        IReadOnlyDictionary<string, string> expectedTexts)
    {
#if LOOMO_EDITOR_EXPECTED_TEXTS
        return new LspWorkspaceEdit(changes, ExpectedTexts: expectedTexts);
#else
        return new LspWorkspaceEdit(changes);
#endif
    }

    private static void ApplyToWorking(
        IDictionary<string, string> working,
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> changes)
    {
        foreach (var (uri, edits) in changes)
        {
            if (LspUri.TryToLocalPath(uri) is not { } path ||
                !TryGetText(working, path, out var text))
                continue;
            working[Path.GetFullPath(path)] = ApplyTextEdits(text!, edits);
        }
    }

    /// <summary>compiler passの編集はbaseline本文を基準にした全文置換で返るため、
    /// StyleCop等の先行変更済み本文へ同じrangeを再適用せず、baselineから最終本文を復元する。</summary>
    private static void ApplyBaselineDocumentEdits(
        IDictionary<string, string> working,
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> changes)
    {
        foreach (var (uri, edits) in changes)
        {
            if (LspUri.TryToLocalPath(uri) is not { } path ||
                !baseline.TryGetValue(Path.GetFullPath(path), out var original) ||
                edits.Count == 0)
                continue;
            working[Path.GetFullPath(path)] = ApplyTextEdits(original, edits);
        }
    }

    private static bool TryGetText(
        IEnumerable<KeyValuePair<string, string>>? texts, string path, out string? value)
    {
        if (texts is not null)
            foreach (var (candidatePath, candidateText) in texts)
                if (string.Equals(Path.GetFullPath(candidatePath), path,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = candidateText;
                    return true;
                }
        value = null;
        return false;
    }

    private static string ApplyTextEdits(string source, IReadOnlyList<LspTextEdit> edits)
    {
        var text = SourceText.From(source);
        var ranges = edits.Select(edit =>
                (edit, start: ToOffset(text, edit.Range.Start), end: ToOffset(text, edit.Range.End)))
            .OrderByDescending(item => item.start)
            .ToArray();
        var result = source;
        var lastStart = int.MaxValue;
        foreach (var item in ranges)
        {
            if (item.start > item.end || item.end > result.Length || item.end > lastStart)
                throw new InvalidOperationException("Fix Allの編集範囲が競合しています。");
            result = result[..item.start] + item.edit.NewText + result[item.end..];
            lastStart = item.start;
        }
        return result;
    }

    private static int ToOffset(SourceText text, LspPosition position)
    {
        var line = Math.Clamp(position.Line, 0, Math.Max(0, text.Lines.Count - 1));
        var textLine = text.Lines[line];
        return Math.Clamp(textLine.Start + position.Character, textLine.Start, textLine.End);
    }

    private static LspRange FullDocumentRange(string source)
    {
        var text = SourceText.From(source);
        var end = text.Lines[^1];
        return new(new(0, 0), new(text.Lines.Count - 1, end.Span.Length));
    }
}

public sealed record CSharpFixAllResult(
    LspWorkspaceEdit? Edit, int DocumentsScanned, int ActionsFound, string? Error = null,
    IReadOnlyDictionary<string, string>? ExpectedTexts = null);
