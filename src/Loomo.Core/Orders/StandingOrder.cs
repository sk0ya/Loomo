using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Orders;

/// <summary>
/// 言いつけ 1 つ（§44.4）＝常設の頼みごと。部屋が裏のワークツリーで Claude Code に渡し、仕上がりを盆に並べる。
/// </summary>
/// <param name="Trigger"><see cref="Manual"/>・<see cref="Daily"/>（<paramref name="At"/> の時刻に 1 日 1 回）・
/// <see cref="Motion"/>（動きが悪い方へ変わったとき＝§44.3）。</param>
/// <param name="Tools">裏の Claude に許す道具（<c>--allowedTools</c>）。既定は読む・書くだけで、シェルは渡さない。</param>
public sealed record StandingOrder(
    string Id, string Prompt, string Trigger, string? At, IReadOnlyList<string> Tools, int MaxRunsPerDay, int TimeoutMinutes)
{
    public const string Manual = "manual";
    public const string Daily = "daily";
    public const string Motion = "motion";

    public static readonly IReadOnlyList<string> DefaultTools = ["Read", "Edit", "Write", "Glob", "Grep"];
    public const int DefaultMaxRunsPerDay = 3;
    public const int DefaultTimeoutMinutes = 30;

    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly Regex TimePattern = new("^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant);

    /// <summary>形を確かめる。おかしければ人間向けの理由。</summary>
    public static string? Validate(string id, string prompt, string trigger, string? at)
    {
        if (!IdPattern.IsMatch(id))
            return "名前は小文字の英数字とハイフンで（40 字まで）";
        if (string.IsNullOrWhiteSpace(prompt))
            return "頼みごとが空です";
        if (trigger is not (Manual or Daily or Motion))
            return "きっかけは「手で」「毎日」「動きが変わったら」のどれかです";
        if (trigger == Daily && (at is null || !TimePattern.IsMatch(at)))
            return "毎日の時刻を HH:mm で";
        return null;
    }

    /// <summary>時刻のきっかけが来ているか：今日まだ走っていなくて、今日の時刻を過ぎた。</summary>
    public bool IsDue(DateTime now, DateTime? lastRun)
    {
        if (Trigger != Daily || At is null || !TimeSpan.TryParse(At, out var time))
            return false;
        var dueAt = now.Date + time;
        return now >= dueAt && (lastRun is null || lastRun.Value < dueAt);
    }
}

/// <summary>言いつけの仕上がり 1 件（盆の 1 行）。</summary>
/// <param name="Status"><see cref="Running"/>・<see cref="Done"/>（変更あり）・<see cref="Empty"/>（変更なし）・<see cref="Failed"/>・
/// <see cref="Taken"/>（採った）。</param>
public sealed record OrderResult(
    string Id, string OrderId, DateTimeOffset At, string Status, string? Branch, string? Worktree,
    string? BaseCommit, string? Commit, int Files, string Summary, string? Motion)
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Empty = "empty";
    public const string Failed = "failed";
    public const string Taken = "taken";
}
