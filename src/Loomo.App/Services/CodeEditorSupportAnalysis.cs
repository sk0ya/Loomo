namespace sk0ya.Loomo.App.Services;

/// <summary>コードEditorSupport用のLSP解析。WPF Viewに依存しない。</summary>
public static class CodeEditorSupportAnalysis
{
    public static async Task<IReadOnlyList<DocumentSymbol>> RequestDocumentSymbolsSafeAsync(IEditorLspManager lsp)
    {
        try { return await lsp.RequestDocumentSymbolsAsync(); }
        catch { return Array.Empty<DocumentSymbol>(); }
    }

    public static int CurrentMemberLine1(IReadOnlyList<OutlineNode> roots, CaretInfo caret)
    {
        var member = CodeOutline.FindEnclosing(roots, caret.Line, caret.Column);
        return member is null ? 0 : member.Line0 + 1;
    }

    public static bool LspMatchesFile(IEditorLspManager lsp, string filePath)
    {
        var current = CodeEditorSupport.TryUriToLocalPath(lsp.CurrentUri);
        if (string.IsNullOrEmpty(current))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(current), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static IReadOnlyList<string> SplitLines(string? text)
        => string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public static bool CaretInRange(LspRange range, int line0, int col0)
    {
        var start = range.Start;
        var end = range.End;
        if (start is null || end is null || line0 < start.Line || line0 > end.Line)
            return false;
        if (line0 == start.Line && col0 < start.Character)
            return false;
        return line0 != end.Line || col0 <= end.Character;
    }

    internal static async Task<(CallPanels Panels, LspRange? SymbolRange)> FetchCallPanelsAsync(
        IEditorLspManager lsp, int line0, int col0)
    {
        async Task<List<CallReference>> FetchReferencesAsync()
        {
            var list = new List<CallReference>();
            try
            {
                foreach (var r in await lsp.RequestReferencesAsync(line0, col0)
                         ?? (IReadOnlyList<LspLocation>)Array.Empty<LspLocation>())
                    if (r is not null)
                        list.Add(new CallReference("", r.Uri ?? "", r.Range?.Start?.Line ?? 0));
            }
            catch { }
            return list;
        }

        var referencesTask = FetchReferencesAsync();
        var incoming = new List<CallReference>();
        var outgoing = new List<CallReference>();
        LspRange? symbolRange = null;
        string? target = null;
        var prepareSw = CodeSupportDiag.IsEnabled ? Stopwatch.StartNew() : null;

        try
        {
            var item = await lsp.PrepareCallHierarchyAsync(line0, col0);
            CodeSupportDiag.Log($"  prepareCallHierarchy {prepareSw?.ElapsedMilliseconds ?? 0}ms item={(item is null ? "null" : item.Name)}");
            if (item is not null)
            {
                symbolRange = item.SelectionRange;
                target = item.Name;

                async Task<List<CallReference>> FetchIncomingAsync()
                {
                    var list = new List<CallReference>();
                    try
                    {
                        foreach (var c in await lsp.GetIncomingCallsAsync(item) ?? Array.Empty<CallHierarchyIncomingCall>())
                            if (c?.From is { } from)
                                list.Add(new CallReference(from.Name ?? "", from.Uri ?? "", from.SelectionRange?.Start?.Line ?? 0));
                    }
                    catch { }
                    return list;
                }

                async Task<List<CallReference>> FetchOutgoingAsync()
                {
                    var list = new List<CallReference>();
                    try
                    {
                        foreach (var c in await lsp.GetOutgoingCallsAsync(item) ?? Array.Empty<CallHierarchyOutgoingCall>())
                            if (c?.To is { } to)
                                list.Add(new CallReference(to.Name ?? "", to.Uri ?? "", to.SelectionRange?.Start?.Line ?? 0));
                    }
                    catch { }
                    return list;
                }

                var callsSw = CodeSupportDiag.IsEnabled ? Stopwatch.StartNew() : null;
                var incomingTask = FetchIncomingAsync();
                var outgoingTask = FetchOutgoingAsync();
                await Task.WhenAll(incomingTask, outgoingTask);
                incoming = incomingTask.Result;
                outgoing = outgoingTask.Result;
                CodeSupportDiag.Log($"  incoming+outgoing {callsSw?.ElapsedMilliseconds ?? 0}ms");
            }
        }
        catch { }

        var refsSw = CodeSupportDiag.IsEnabled ? Stopwatch.StartNew() : null;
        var references = await referencesTask;
        CodeSupportDiag.Log($"  references(await) {refsSw?.ElapsedMilliseconds ?? 0}ms count={references.Count}");
        return (new CallPanels(incoming, outgoing, references, target), symbolRange);
    }
}
