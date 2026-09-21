namespace sk0ya.Loomo.App.Services;

/// <summary>複数のファイル項目への操作を一つの履歴単位として実行する。</summary>
internal static class FileEntryBatchExecutor
{
    public static string? Execute<T>(
        IEnumerable<T> entries,
        Func<IDisposable> beginBatch,
        Func<T, string?> executeEntry)
    {
        string? lastResult = null;
        using (beginBatch())
            foreach (var entry in entries)
            {
                try { lastResult = executeEntry(entry) ?? lastResult; }
                catch (InvalidOperationException ex) { ToastService.Error(ex.Message); }
            }

        return lastResult;
    }
}
