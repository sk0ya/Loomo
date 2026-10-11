using System.Globalization;

namespace sk0ya.Loomo.App.Services;

/// <summary>ローカル履歴をパレットの行・差分の見出しへ写す純ロジック（テスト対象・§36）。</summary>
public static class LocalHistoryPresentation
{
    /// <summary>「3分前」「昨日 14:05」のような、行の左に置く短い時刻。</summary>
    public static string RelativeTime(DateTime timestampUtc, DateTime nowUtc)
    {
        var local = timestampUtc.ToLocalTime();
        var now = nowUtc.ToLocalTime();
        var elapsed = now - local;
        if (elapsed < TimeSpan.FromMinutes(1)) return "たった今";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}分前";
        if (local.Date == now.Date) return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == now.Date.AddDays(-1)) return "昨日 " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>差分の左側の見出し（「保存 10/11 14:05:12」）。</summary>
    public static string VersionTitle(LocalHistoryEntry entry)
        => $"{entry.KindLabel} {entry.TimestampUtc.ToLocalTime().ToString("M/d HH:mm:ss", CultureInfo.InvariantCulture)}";

    /// <summary>行数の増減（「+3 −1 行」）。中身を比べた結果で、差分の厳密な行数ではない。</summary>
    public static string LineDelta(string version, string current)
    {
        var before = CountLines(version);
        var after = CountLines(current);
        var delta = after - before;
        return delta == 0 ? $"{before} 行" : $"{before} 行（今は {(delta > 0 ? "+" : "−")}{Math.Abs(delta)}）";
    }

    /// <summary>文書全体を置き換える 1 つの編集の範囲（0 始まり・終わりは最終行の末尾）。</summary>
    public static LspRange WholeDocumentRange(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        return new LspRange(new LspPosition(0, 0), new LspPosition(lines.Length - 1, lines[^1].Length));
    }

    private static int CountLines(string text)
        => text.Length == 0 ? 0 : text.Count(ch => ch == '\n') + (text.EndsWith('\n') ? 0 : 1);
}
