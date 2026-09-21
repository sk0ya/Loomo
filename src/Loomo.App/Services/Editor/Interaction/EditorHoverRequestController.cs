namespace sk0ya.Loomo.App.Services;

/// <summary>hoverの要求順序を管理し、LSPが空応答の場合だけfallbackへ進む。</summary>
internal sealed class EditorHoverRequestController
{
    private long _requestId;

    public long BeginRequest() => Interlocked.Increment(ref _requestId);

    public bool IsCurrent(long requestId)
        => Volatile.Read(ref _requestId) == requestId;

    public async Task<string?> RequestTextAsync(
        Func<Task<string?>>? requestLsp,
        Func<Task<string?>> requestFallback)
    {
        if (requestLsp is not null)
        {
            try
            {
                var value = await requestLsp();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                // LSPが応えないときはfallbackを試す。
            }
        }

        return await requestFallback();
    }
}
