namespace sk0ya.Loomo.App.Services;

internal readonly record struct FilePasteBatchOutcome(
    bool Completed,
    bool Cancelled,
    string? LastDestinationPath);

/// <summary>複数項目の貼り付けを一つの履歴単位として実行する。</summary>
internal static class FilePasteBatchExecutor
{
    public static FilePasteBatchOutcome Execute(
        Func<IDisposable> beginBatch,
        IEnumerable<string> sources,
        bool move,
        Func<string, bool, Func<FileConflictContext, FileConflictDecision>, FilePasteResult> pasteEntry,
        Func<FileConflictContext, FileConflictDecision> resolveConflict)
    {
        var conflictResolver = new FileConflictBatchResolver(resolveConflict);
        string? lastDestinationPath = null;
        var cancelled = false;

        try
        {
            using (beginBatch())
                foreach (var source in sources)
                    if (!string.IsNullOrEmpty(source))
                    {
                        var result = pasteEntry(source, move, conflictResolver.Resolve);
                        if (result.DestinationPath is { } destination)
                            lastDestinationPath = destination;
                        if (result.Cancelled)
                        {
                            cancelled = true;
                            break;
                        }
                    }
        }
        catch (InvalidOperationException ex)
        {
            ToastService.Error(ex.Message);
            return new(false, cancelled, lastDestinationPath);
        }

        return new(true, cancelled, lastDestinationPath);
    }
}
