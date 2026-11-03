using System;
using System.Collections.Generic;

namespace sk0ya.Loomo.Services;

/// <summary>行の付け直しの結果。<paramref name="Line"/> は 1 始まり、<paramref name="Exact"/> は中身で見つけたか（false なら行番号のまま）。</summary>
public readonly record struct ReviewLineMatch(int Line, bool Exact);

/// <summary>
/// レビューのコメントの行（PR の版の行番号）を、手元のファイルの行へ付け直す（純粋）。手元が PR の版より進んでいたり、
/// 編集中だったりすると、行番号のままでは違う行に付く。PR の版のその行の中身を、手元で<b>前後の行も含めて一番よく合う</b>
/// 所に探す——<c>}</c> や空行のようなありふれた行でも、前後の行で見分ける。見つからなければ行番号のまま（範囲に収めて）。
/// </summary>
public static class ReviewLineAnchor
{
    /// <summary>探す範囲（元の行の上下）。遠くの同じ行に飛ばない。</summary>
    public const int SearchRadius = 400;

    public static ReviewLineMatch Map(int line, IReadOnlyList<string>? prLines, IReadOnlyList<string> localLines)
    {
        var fallback = new ReviewLineMatch(Math.Clamp(line, 1, Math.Max(1, localLines.Count)), false);
        if (prLines is null || line < 1 || line > prLines.Count || localLines.Count == 0)
            return fallback;
        var target = prLines[line - 1].Trim();
        if (target.Length == 0)
            return fallback;

        var best = -1;
        var bestScore = -1;
        var bestDistance = int.MaxValue;
        var from = Math.Max(0, line - 1 - SearchRadius);
        var to = Math.Min(localLines.Count - 1, line - 1 + SearchRadius);
        for (var i = from; i <= to; i++)
        {
            if (!string.Equals(localLines[i].Trim(), target, StringComparison.Ordinal))
                continue;
            var score = 0;
            for (var k = -2; k <= 2; k++)
            {
                if (k == 0) continue;
                var p = line - 1 + k;
                var q = i + k;
                if (p >= 0 && p < prLines.Count && q >= 0 && q < localLines.Count
                    && string.Equals(prLines[p].Trim(), localLines[q].Trim(), StringComparison.Ordinal))
                    score++;
            }
            var distance = Math.Abs(i - (line - 1));
            if (score > bestScore || (score == bestScore && distance < bestDistance))
            {
                best = i;
                bestScore = score;
                bestDistance = distance;
            }
        }
        return best >= 0 ? new ReviewLineMatch(best + 1, true) : fallback;
    }
}
