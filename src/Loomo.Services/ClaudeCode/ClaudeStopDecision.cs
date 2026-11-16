using System.Text.Json.Nodes;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// 「動きが悪くなったら Claude に直させる」（§45.6 P3）の判定。Stop フックに返す <c>decision: block</c> を組むだけの純粋な関数。
/// 1 つの頼みごとの中で続けさせるのは最大 <see cref="MaxContinuationsPerPrompt"/> 回まで——直らないのに無限に続けない。
/// </summary>
public static class ClaudeStopDecision
{
    public const int MaxContinuationsPerPrompt = 2;

    /// <summary>止めずに続けさせるなら、Stop フックの返事。止めてよいなら null。</summary>
    /// <param name="headline">悪くなった動きの要約（<c>MotionDiff.Headline</c>）。悪化していなければ null。</param>
    /// <param name="continuationsSoFar">この頼みごとで既に続けさせた回数。</param>
    public static JsonObject? Block(string? headline, int continuationsSoFar)
    {
        if (string.IsNullOrWhiteSpace(headline) || continuationsSoFar >= MaxContinuationsPerPrompt)
            return null;
        return new JsonObject
        {
            ["decision"] = "block",
            ["reason"] = "Loomo の動きの記録で、直前のターンのあと悪い方へ変わりました：" + headline
                         + "。直してから終わってください（直せない理由があれば、そう書いて終わってください）。",
        };
    }
}
