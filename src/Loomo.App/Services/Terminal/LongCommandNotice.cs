namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 時間のかかったコマンドが、見ていない間に終わったら知らせる（§24.97・Contour 0.8.0 の finish notifications）。
/// 袖のバッジは部屋の中にいるときの周辺視野、留守中のまとめ（§24.22）は 5 分以上の離席——その間
/// （別のアプリで数分作業している間に終わったビルド）を通知欄とタスクバーで埋める。純関数。
/// </summary>
internal static class LongCommandNotice
{
    /// <summary>これより短いコマンドは知らせない（打ってすぐ終わるものを通知欄に積まない）。</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(10);

    /// <summary>知らせるか。<paramref name="watched"/> は「Loomo が前面で、ターミナルペインが見えていて、
    /// そのタブが前に出ている」——そのときは人が終わるのを見ているので知らせない。</summary>
    public static bool ShouldNotify(bool enabled, TimeSpan? duration, bool watched)
        => enabled && !watched && duration is { } d && d >= Threshold;

    /// <summary>通知欄の 1 件の本文：「dotnet test が失敗しました（終了コード 1・2分14秒）」。</summary>
    public static string Message(string? commandLine, int? exitCode, TimeSpan duration)
    {
        var command = Shorten(string.IsNullOrWhiteSpace(commandLine) ? "コマンド" : commandLine.Trim());
        var time = FormatDuration(duration);
        return exitCode is { } code && code != 0
            ? $"{command} が失敗しました（終了コード {code}・{time}）"
            : $"{command} が終わりました（{time}）";
    }

    public static NotificationKind Kind(int? exitCode)
        => exitCode is { } code && code != 0 ? NotificationKind.Error : NotificationKind.Success;

    /// <summary>「8秒」「2分14秒」「1時間3分」。</summary>
    public static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours}時間{d.Minutes}分";
        if (d.TotalMinutes >= 1)
            return $"{(int)d.TotalMinutes}分{d.Seconds}秒";
        return $"{Math.Max(0, (int)d.TotalSeconds)}秒";
    }

    private const int MaxCommandChars = 60;

    private static string Shorten(string command)
    {
        var line = command.ReplaceLineEndings(" ");
        return line.Length <= MaxCommandChars ? line : line[..(MaxCommandChars - 1)] + "…";
    }
}
