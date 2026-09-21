namespace sk0ya.Loomo.App.Services;

/// <summary>UI が追記する分割文書の進捗とキャンセル状態。</summary>
internal sealed class ChunkedAppendState
{
    private readonly Action<int, int> _append;
    private readonly int _rowCount;
    private int _next;

    public ChunkedAppendState(int rowCount, Action<int, int> append)
    {
        _rowCount = rowCount;
        _append = append;
    }

    public bool IsRunning => _next < _rowCount && !Cancelled;
    public bool Cancelled { get; private set; }
    public void Cancel() => Cancelled = true;

    public void Step(int chunk)
    {
        if (Cancelled || chunk <= 0) return;
        var end = chunk >= _rowCount - _next ? _rowCount : _next + chunk;
        if (end == _next) return;
        _append(_next, end);
        _next = end;
    }

    public void Finish() => Step(_rowCount);
}
