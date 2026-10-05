using System.Text;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 引用した一節へ戻るための URL（Text Fragment・<c>#:~:text=</c>）を組み立てる（§24.24）。
/// Chromium（WebView2）はこの URL で開くと該当箇所までスクロールして強調するので、ペグボードに残した
/// ブラウザの抜き書きから「どこに書いてあったか」へそのまま戻れる。見つからなければ普通にページが開くだけ
/// ——壊れても失うものが無いので、照合の厳密さより URL の短さを取る。
/// </summary>
internal static class BrowserTextFragment
{
    /// <summary>この長さ以下の1行はまるごと一致させる（<c>text=全文</c>）。</summary>
    private const int ExactMaxChars = 100;

    /// <summary>範囲指定（<c>text=先頭,末尾</c>）にするときの先頭・末尾それぞれの長さの目安。</summary>
    private const int EdgeChars = 40;

    /// <summary><paramref name="url"/> に <paramref name="quote"/> を指す Text Fragment を付けた URL。
    /// http(s) 以外・引用が空なら元の URL のまま。既存の Text Fragment は置き換える。</summary>
    public static string Build(string url, string quote)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        var directive = Directive(quote);
        if (directive is null)
            return url;

        var hash = url.IndexOf('#');
        if (hash < 0)
            return $"{url}#:~:text={directive}";
        var marker = url.IndexOf(":~:", hash, StringComparison.Ordinal);
        var head = marker < 0 ? url : url[..marker];
        return $"{head}:~:text={directive}";
    }

    /// <summary><c>text=</c> の値（エンコード済み）。照合できる文字が無ければ null。</summary>
    internal static string? Directive(string quote)
    {
        // 完全一致はブロック（段落）をまたげないので、行ごとに分けて空白を畳む。
        var lines = quote.Replace("\r\n", "\n").Split('\n')
            .Select(CollapseWhitespace)
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count == 0)
            return null;

        if (lines.Count == 1 && lines[0].Length <= ExactMaxChars)
            return Encode(lines[0]);

        // 範囲指定：先頭行の頭から、末尾行のお尻まで。
        var start = Head(lines[0]);
        var end = Tail(lines[^1]);
        return $"{Encode(start)},{Encode(end)}";
    }

    private static string CollapseWhitespace(string line)
    {
        var builder = new StringBuilder(line.Length);
        var pendingSpace = false;
        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>行の頭から目安の長さまで。空白区切りの文なら語の途中で切らない（照合は語境界を見るため）。</summary>
    private static string Head(string line)
    {
        if (line.Length <= EdgeChars)
            return line;
        var cut = line.LastIndexOf(' ', EdgeChars);
        return cut >= EdgeChars / 2 ? line[..cut] : line[..EdgeChars];
    }

    /// <summary>行のお尻から目安の長さまで（<see cref="Head"/> の逆向き）。</summary>
    private static string Tail(string line)
    {
        if (line.Length <= EdgeChars)
            return line;
        var from = line.Length - EdgeChars;
        var cut = line.IndexOf(' ', from);
        return cut >= 0 && cut <= line.Length - EdgeChars / 2 ? line[(cut + 1)..] : line[from..];
    }

    /// <summary>パーセントエンコード。<c>-</c> と <c>,</c> と <c>&amp;</c> は指令の区切りなので必ずエンコードする
    /// （<see cref="Uri.EscapeDataString"/> は <c>-</c> を素通しする）。</summary>
    private static string Encode(string text)
        => Uri.EscapeDataString(text).Replace("-", "%2D");
}
