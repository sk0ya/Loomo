namespace sk0ya.Loomo.App.Services;

/// <summary>ポップアップを閉じた直後、同じクリックで再度開くのを抑止する時間判定。</summary>
internal static class PopupReopenPolicy
{
    private static readonly TimeSpan GuardDuration = TimeSpan.FromMilliseconds(250);

    public static bool WasClosedRecently(DateTime closedAt, DateTime now)
        => now - closedAt < GuardDuration;
}
