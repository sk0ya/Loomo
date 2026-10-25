namespace sk0ya.Loomo.Core.Pty;

/// <summary>画面の文字（スクロールバック込み）から語を探す（純ロジック・§24.67）。常駐ホストが、まだ表示していないタブの画面を探すのに使う。</summary>
public static class TerminalScreenSearch
{
    /// <summary>大文字小文字を区別せずに探し、当たった行を上から返す（1 行に何度当たっても 1 件）。</summary>
    public static IReadOnlyList<PtyProtocol.SearchMatch> Find(Guid sessionId, string screenText, string query, int limit)
    {
        var matches = new List<PtyProtocol.SearchMatch>();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(screenText))
            return matches;
        var lines = screenText.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length && matches.Count < limit; i++)
        {
            var column = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (column >= 0)
                matches.Add(new PtyProtocol.SearchMatch(sessionId, i, lines[i].TrimEnd(), column));
        }
        return matches;
    }
}
