using System.Windows;

namespace sk0ya.Loomo.App.Services;

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>通知に添える「その場でできること」（例：「開く」「再試行」）。押されると UI スレッドで <see cref="Run"/> を呼ぶ。</summary>
public sealed record NotificationAction(string Label, Action Run);

/// <summary>1件の知らせ。トーストとして一瞬出るか（<see cref="ShowToast"/>）に関わらず、軌跡バー右端の通知欄の履歴に残る。</summary>
public sealed record Notification(
    Guid Id,
    string Message,
    NotificationKind Kind,
    DateTime Timestamp,
    string? Source = null,
    NotificationAction? Action = null,
    bool ShowToast = true);

public sealed class NotificationPostedEventArgs : EventArgs
{
    public NotificationPostedEventArgs(Notification notification) => Notification = notification;

    public Notification Notification { get; }
}

/// <summary>部屋の知らせの発行口（設計書 §21.5c）。ここに出したものは軌跡バー右端の通知欄
/// （<see cref="ViewModels.NotificationCenterViewModel"/>）に履歴として残り、<see cref="Notification.ShowToast"/>
/// なら右下のトースト（<see cref="ViewModels.ToastHostViewModel"/>）にも一瞬出る。
/// <see cref="ToastService"/> はこの上の薄い入口で、既存の呼び出しはそのまま履歴にも載る。
/// <para>どのスレッドから呼んでもよい——購読者は UI の一覧を触るので、UI スレッド以外からの発行は
/// ディスパッチャへ回す（WPF アプリが無いテストでは呼んだスレッドでそのまま流す）。
/// 購読者がいなければ黙って捨てる作りは <see cref="ToastService"/> から変えていない（App.OnStartup 参照）。</para></summary>
public static class NotificationService
{
    public static event EventHandler<NotificationPostedEventArgs>? Posted;

    /// <summary>知らせを出す。<paramref name="toast"/> が false なら右下には出さず、通知欄の履歴と未読件数にだけ載せる
    /// （作業を遮るほどではないが、後で見返せてほしいこと向け）。</summary>
    public static Notification Post(
        string message,
        NotificationKind kind = NotificationKind.Info,
        string? source = null,
        NotificationAction? action = null,
        bool toast = true)
    {
        var notification = new Notification(Guid.NewGuid(), message, kind, DateTime.Now, source, action, toast);
        Raise(notification);
        return notification;
    }

    public static Notification Info(string message, string? source = null, NotificationAction? action = null)
        => Post(message, NotificationKind.Info, source, action);

    public static Notification Success(string message, string? source = null, NotificationAction? action = null)
        => Post(message, NotificationKind.Success, source, action);

    public static Notification Warning(string message, string? source = null, NotificationAction? action = null)
        => Post(message, NotificationKind.Warning, source, action);

    public static Notification Error(string message, string? source = null, NotificationAction? action = null)
        => Post(message, NotificationKind.Error, source, action);

    private static void Raise(Notification notification)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Posted?.Invoke(null, new NotificationPostedEventArgs(notification)));
            return;
        }
        Posted?.Invoke(null, new NotificationPostedEventArgs(notification));
    }
}
