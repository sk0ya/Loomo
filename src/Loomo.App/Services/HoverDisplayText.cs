namespace sk0ya.Loomo.App.Services;

/// <summary>hover（この位置の説明）の本文を、そのまま読める素のテキストに直す。
///
/// <para>言語サーバーは hover を <b>Markdown</b> で返す（<c>```csharp … ```</c> のコードフェンスに
/// シグネチャを入れ、その下に要約を書く）。Editor 側はそれをステータスバーへ
/// <b>先頭 1 行だけ</b>流していたため、実測で表示されるのは <c>```csharp</c> というフェンスそのもの
/// だった——「説明を表示」が何も説明していない状態。ここでフェンスと空行の連続を落として、
/// シグネチャと要約だけを残す。</para>
///
/// <para>Roslyn フォールバック（<c>CSharpHoverService</c>）は素のテキストを返すので、
/// その場合はほぼ素通しになる。</para></summary>
internal static class HoverDisplayText
{
    /// <summary>表示用の本文。中身が無ければ null。</summary>
    internal static string? Plain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            // コードフェンス（```csharp / ```）は本文ではなく囲いなので落とす。
            if (trimmed.TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;
            // 空行が続くのは Markdown の段落分けの都合。1 行に畳む。
            if (trimmed.Trim().Length == 0 && (kept.Count == 0 || kept[^1].Length == 0)) continue;
            kept.Add(Unescape(trimmed));
        }
        while (kept.Count > 0 && kept[^1].Length == 0)
            kept.RemoveAt(kept.Count - 1);

        var text = string.Join(Environment.NewLine, kept);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Markdown のエスケープを外す。サーバーは識別子の <c>_</c> を <c>\_</c> と書いて返すので
    /// （実測: <c>'\_value' は null ではありません</c>）、そのまま出すと<b>コードに無いバックスラッシュ</b>が
    /// 混ざる。外すのは Markdown が定める記号の前の 1 文字だけで、それ以外の <c>\</c> は本文なので残す。</summary>
    private static string Unescape(string line)
    {
        if (!line.Contains('\\')) return line;
        var text = new System.Text.StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length && EscapablePunctuation.Contains(line[i + 1]))
                continue;
            text.Append(line[i]);
        }
        return text.ToString();
    }

    private const string EscapablePunctuation = @"\`*_{}[]()#+-.!<>|~";
}
