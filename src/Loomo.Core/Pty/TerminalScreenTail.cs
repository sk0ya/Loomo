namespace sk0ya.Loomo.Core.Pty;

/// <summary>画面の文字（スクロールバック込み）から末尾の行を切り出す（純ロジック・§24.108）。常駐ホストが、覗く（タブを切り替えずに末尾を読む）のに使う。</summary>
public static class TerminalScreenTail
{
    /// <summary>
    /// 末尾の <paramref name="lines"/> 行。各行の行末の空白と、画面の下に残った空の行は落とす——カーソルが画面の途中にあると、
    /// その下は空の行が並ぶだけで、覗いたときに肝心の行が上へ押し出される。
    /// </summary>
    public static string Last(string screenText, int lines)
    {
        if (lines <= 0 || string.IsNullOrEmpty(screenText))
            return "";
        var all = screenText.Replace("\r\n", "\n").Split('\n');
        var end = all.Length;
        while (end > 0 && string.IsNullOrWhiteSpace(all[end - 1]))
            end--;
        var start = Math.Max(0, end - lines);
        return string.Join("\n", all[start..end].Select(line => line.TrimEnd()));
    }
}
