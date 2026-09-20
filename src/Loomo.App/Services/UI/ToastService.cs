namespace sk0ya.Loomo.App.Services;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

public sealed class ToastRequestedEventArgs : EventArgs
{
    public ToastRequestedEventArgs(string message, ToastKind kind)
    {
        Message = message;
        Kind = kind;
    }

    public string Message { get; }
    public ToastKind Kind { get; }
}

/// <summary>非モーダルなトースト通知の発行口。単純な結果通知（成功・失敗）はここを通す。
/// ユーザーの決定（Yes/No・OK/Cancel）が要る確認は引き続き MessageBox.Show を使う
/// （トーストは自動で消えるため、応答を待つ必要がある操作には使えない）。</summary>
public static class ToastService
{
    public static event EventHandler<ToastRequestedEventArgs>? Requested;

    public static void Info(string message) => Raise(message, ToastKind.Info);
    public static void Success(string message) => Raise(message, ToastKind.Success);
    public static void Warning(string message) => Raise(message, ToastKind.Warning);
    public static void Error(string message) => Raise(message, ToastKind.Error);

    private static void Raise(string message, ToastKind kind)
        => Requested?.Invoke(null, new ToastRequestedEventArgs(message, kind));
}
