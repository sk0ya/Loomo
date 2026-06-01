namespace sk0ya.Loomo.Core.Observability;

/// <summary>何もしないトレースシンク（既定）。トレース無効時・テスト時に使う。</summary>
public sealed class NullTraceSink : ITraceSink
{
    public static readonly NullTraceSink Instance = new();

    private NullTraceSink() { }

    public void Record(string sessionId, string? turnId, string kind, object? payload) { }
}
