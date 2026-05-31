using System.Collections.Generic;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// コンテキストウィンドウ超過を防ぐため、会話履歴を予算トークン内に収まるよう古い方から切り詰める。
/// 純粋関数（副作用なし・決定的）として単体テスト可能。
///
/// 不変条件: 切り詰め後の先頭は必ず <see cref="ChatRole.User"/> メッセージにする。
/// これにより (1) Anthropic/OpenAI が要求する「会話は user から始まる」を満たし、
/// (2) 直前の assistant の tool_use が落ちて tool_result だけが孤立する状態を防ぐ
/// （tool_result を含む <see cref="ChatRole.Tool"/> は user メッセージより後ろにしか現れないため）。
/// </summary>
public static class ConversationTrimmer
{
    /// <summary>
    /// <paramref name="messages"/> を <paramref name="budgetTokens"/> 内に収まるよう末尾優先で残す。
    /// 予算が0以下、または全体が予算内なら元の列をそのまま返す。
    /// </summary>
    public static IReadOnlyList<ChatMessage> Trim(IReadOnlyList<ChatMessage> messages, int budgetTokens)
    {
        if (budgetTokens <= 0 || messages.Count == 0)
            return messages;

        if (TokenEstimator.EstimateMessages(messages) <= budgetTokens)
            return messages;

        // 末尾から予算ぶんだけ積み上げ、収まる最小の開始位置を求める。
        var running = 0;
        var start = messages.Count; // 何も残せなかった場合の番兵
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var next = running + TokenEstimator.EstimateMessage(messages[i]);
            if (next > budgetTokens)
                break;
            running = next;
            start = i;
        }

        // 先頭が user になるまで前方へ詰める（孤立 tool_result / 先頭 assistant を排除）。
        while (start < messages.Count && messages[start].Role != ChatRole.User)
            start++;

        // 予算が極端に小さく1メッセージも残せない場合は、最後の user 以降を最低限残す。
        if (start >= messages.Count)
        {
            for (var i = messages.Count - 1; i >= 0; i--)
                if (messages[i].Role == ChatRole.User)
                    return Slice(messages, i);
            return messages; // user が無い異常系は素通し
        }

        return Slice(messages, start);
    }

    private static IReadOnlyList<ChatMessage> Slice(IReadOnlyList<ChatMessage> messages, int start)
    {
        if (start <= 0)
            return messages;
        var kept = new List<ChatMessage>(messages.Count - start);
        for (var i = start; i < messages.Count; i++)
            kept.Add(messages[i]);
        return kept;
    }
}
