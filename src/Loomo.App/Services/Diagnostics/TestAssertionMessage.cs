using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

/// <summary>失敗したテストのメッセージから読み取った「期待値」と「実際」。</summary>
internal sealed record TestAssertionValues(string Expected, string Actual);

/// <summary>期待値と実際を Diff ペインの比較（§24.5）にする。左＝期待値・右＝実際（「こうあるべき」から「こうなった」への差分）。</summary>
internal static class TestAssertionComparison
{
    public static sk0ya.Loomo.App.ViewModels.DiffComparison For(string testName, TestAssertionValues values)
        => new("期待値", values.Expected, "実際", values.Actual)
        {
            Label = $"{testName}：期待値 ↔ 実際",
        };
}

/// <summary>
/// 失敗したテストのメッセージから、期待値と実際の値を切り出す（差分ペインで比べるため・§24.110）。
/// 読めるのは主なテストフレームワークの「値を 2 つ並べる」形だけで、読めなければ null（右クリックに項目を出さない）。
/// <list type="bullet">
/// <item>xUnit：<c>Expected: …</c>／<c>Actual: …</c>（間の <c>↓ (pos 3)</c>・<c>↑</c> の行は捨てる）</item>
/// <item>NUnit：<c>Expected: …</c>／<c>But was: …</c>（<c>-----^</c> の行は捨てる）</item>
/// <item>MSTest：<c>Expected:&lt;…&gt;. Actual:&lt;…&gt;.</c></item>
/// <item>Jest／Vitest：<c>Expected: …</c>／<c>Received: …</c></item>
/// </list>
/// 値が <c>"…"</c> で囲まれていれば外して <c>\n</c> 等を戻す——複数行の文字列が行ごとの差分になる。
/// </summary>
internal static class TestAssertionMessage
{
    private static readonly Regex MsTest = new(
        @"Expected:<(?<e>.*?)>\.?\s+Actual:<(?<a>.*)>\.?",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Label = new(
        @"^\s*(?<label>Expected|Actual|But was|Received)\s*:\s?(?<value>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    /// <summary>xUnit の位置の印（<c>↓ (pos 3)</c>）と NUnit の印（<c>-----^</c>）。</summary>
    private static readonly Regex Marker = new(@"^\s*(?:[↓↑]\s*(?:\(pos \d+\))?|-*\^)\s*$", RegexOptions.Compiled);

    public static TestAssertionValues? TryParse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var text = Ansi.Replace(message, "").Replace("\r\n", "\n");

        var ms = MsTest.Match(text);
        if (ms.Success)
            return Make(ms.Groups["e"].Value, ms.Groups["a"].Value, unquote: false);

        string? expected = null, actual = null;
        StringBuilder? current = null;
        var isExpected = false;
        foreach (var line in text.Split('\n'))
        {
            var m = Label.Match(line);
            if (m.Success)
            {
                Flush();
                var label = m.Groups["label"].Value;
                isExpected = label == "Expected";
                if (!isExpected && expected is null)
                    return null;   // 実際が先に来る形は知らない
                current = new StringBuilder(m.Groups["value"].Value.TrimEnd());
                continue;
            }
            if (current is null)
                continue;
            if (Marker.IsMatch(line) || line.Trim().Length == 0 || line.StartsWith("   at ", StringComparison.Ordinal))
            {
                Flush();
                if (actual is not null)
                    break;
                continue;
            }
            // 値の続きの行（コレクションの改行など）。
            current.Append('\n').Append(line.TrimEnd());
        }
        Flush();
        return expected is null || actual is null ? null : Make(expected, actual, unquote: true);

        void Flush()
        {
            if (current is null)
                return;
            if (isExpected) expected ??= current.ToString();
            else actual ??= current.ToString();
            current = null;
        }
    }

    private static TestAssertionValues? Make(string expected, string actual, bool unquote)
    {
        if (unquote)
        {
            expected = Unquote(expected.Trim());
            actual = Unquote(actual.Trim());
        }
        // 同じなら比べる意味が無い（読み違い）。両方空も同じ。
        return string.Equals(expected, actual, StringComparison.Ordinal) ? null : new TestAssertionValues(expected, actual);
    }

    /// <summary><c>"a\nb"</c> → 2 行の a と b。囲みが無ければそのまま。xUnit が長い文字列の末尾に付ける <c>···</c> も外す。</summary>
    internal static string Unquote(string value)
    {
        if (value.EndsWith("···", StringComparison.Ordinal))
            value = value[..^3];
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return value;
        var inner = value[1..^1];
        var sb = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c != '\\' || i + 1 >= inner.Length)
            {
                sb.Append(c);
                continue;
            }
            var next = inner[++i];
            sb.Append(next switch
            {
                'n' => "\n",
                'r' => "",
                't' => "\t",
                '"' => "\"",
                '\\' => "\\",
                '0' => "\0",
                _ => "\\" + next,
            });
        }
        return sb.ToString();
    }
}
