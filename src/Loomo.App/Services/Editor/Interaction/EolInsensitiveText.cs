using System;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 改行の綴り（CRLF／CR／LF）を無視した本文の比較。
/// <para>
/// 「ディスクの内容と開いているバッファは同じか」を見るだけなら、正規化した文字列を<b>作る</b>必要はない。
/// 作っていた頃は 1 回の判定で全文の複製が 3 本（ディスク側・バッファ側・バッファの取り出し）走り、
/// ブランチ切替や一括置換のように<b>開いている全タブ</b>を見る経路では、それがタブ数ぶん UI スレッドで
/// 積み上がっていた（456KB のファイルで 1 タブ 0.51ms・約 1.4MB・すべて LOH 行き）。§31.15
/// </para>
/// <para>
/// <b>1 文字ずつ回してはいけない。</b>最初にそう書いたら 456KB で <b>4.97ms</b>——作って比べるより 10 倍遅かった。
/// <see cref="string.Equals(string, string, StringComparison)"/> や <see cref="MemoryExtensions.SequenceEqual"/>
/// は SIMD で一度に多文字を見るので、素朴なループはそれに敵わない。だからここは「行ごとに区切って、
/// 区切りの中は span の一括比較に任せる」形にしてある（0.46ms・<b>割り当て 0</b>）。速さより、
/// 打鍵中に 1.4MB を LOH へ捨てなくなることが効く。
/// </para>
/// </summary>
internal static class EolInsensitiveText
{
    /// <summary>改行の綴りだけが違う本文を同じとみなして比較する（割り当てなし）。</summary>
    public static bool Equals(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        // CR がどちらにも無ければ綴りの違いは起こり得ないので、丸ごと1回の比較で済む（最速の道）。
        if (!left.Contains('\r') && !right.Contains('\r'))
            return string.Equals(left, right, StringComparison.Ordinal);

        return EqualsLineByLine(left, right);
    }

    private static bool EqualsLineByLine(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        while (true)
        {
            var l = left.IndexOfAny('\r', '\n');
            var r = right.IndexOfAny('\r', '\n');
            // どちらかに改行が無ければ、そこが最後の行。残りが一致するかで決まる。
            if (l < 0 || r < 0)
                return l == r && left.SequenceEqual(right);
            if (l != r || !left[..l].SequenceEqual(right[..r]))
                return false;
            left = left[(l + BreakLength(left, l))..];
            right = right[(r + BreakLength(right, r))..];
        }
    }

    /// <summary>その位置から始まる改行の長さ（CRLF なら 2、CR／LF なら 1）。</summary>
    private static int BreakLength(ReadOnlySpan<char> text, int index)
        => text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
}
