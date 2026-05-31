using System.Collections.Generic;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// トークン数の概算。正確なトークナイザは持たないため「文字数 / 4 ＋ 固定オーバーヘッド」の
/// 経験則で見積もる（英数字寄りでは過小、日本語では過大になりがちだが、コンテキスト超過を
/// 防ぐための上限管理用途では安全側に倒れていれば十分）。決定的なので単体テスト可能。
/// </summary>
public static class TokenEstimator
{
    /// <summary>1メッセージあたりのロール/区切り等のオーバーヘッド。</summary>
    private const int MessageOverhead = 4;

    /// <summary>テキストの概算トークン数（4文字 ≒ 1トークン）。</summary>
    public static int EstimateText(string? text)
        => string.IsNullOrEmpty(text) ? 0 : (text!.Length + 3) / 4;

    /// <summary>1メッセージ（本文＋ツール呼び出し＋ツール結果）の概算。</summary>
    public static int EstimateMessage(ChatMessage message)
    {
        var tokens = MessageOverhead + EstimateText(message.Text);
        foreach (var use in message.ToolUses)
            tokens += EstimateText(use.Name) + EstimateText(use.ArgumentsJson);
        foreach (var result in message.ToolResults)
            tokens += EstimateText(result.Content);
        return tokens;
    }

    /// <summary>メッセージ列全体の概算。</summary>
    public static int EstimateMessages(IEnumerable<ChatMessage> messages)
    {
        var total = 0;
        foreach (var m in messages)
            total += EstimateMessage(m);
        return total;
    }
}
