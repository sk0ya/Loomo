namespace sk0ya.Loomo.App.Services;

/// <summary>ターミナルのコマンドが出した通知（OSC 9・OSC 777）を通知欄の 1 件にする形（§24.49）。
/// 題と本文は Terminal パッケージが改行・制御文字を空白にして長さを切ってある。</summary>
internal static class TerminalNotificationPresentation
{
    /// <summary>本文（題があれば「題: 本文」）。</summary>
    public static string Message(string? title, string body)
        => string.IsNullOrWhiteSpace(title) ? body
            : string.IsNullOrWhiteSpace(body) ? title
            : $"{title}: {body}";

    /// <summary>出所（通知欄の小さい見出し）。タブ名が分からなければ「ターミナル」だけ。</summary>
    public static string Source(string? tabTitle)
        => string.IsNullOrWhiteSpace(tabTitle) ? "ターミナル" : $"ターミナル: {tabTitle}";
}

/// <summary>軌跡でターミナルの地点へ戻ったとき、その頃の出力へ飛ぶか（§24.51）。</summary>
internal static class TerminalLineTimePolicy
{
    /// <summary>これより新しい地点では飛ばない——いまに近い地点で飛ぶと、流れている出力の追従を止めてしまう。</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(2);

    public static bool ShouldScrollTo(DateTime entryTime, DateTime now) => now - entryTime >= RecentWindow;
}
