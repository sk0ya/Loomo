using System.Collections.Generic;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// AI へ送る直前に会話をコンテキスト予算へ収めるポリシー。
/// 実体（プロバイダ別の予算）は上位層（Ai）が <see cref="AiSettings"/> を見て決める。
/// Core はインターフェースと無加工の既定実装だけを持つ。
/// </summary>
public interface IContextWindowPolicy
{
    /// <summary>
    /// 送信用に整えた会話を返す。元の <paramref name="conversation"/> は変更しない
    /// （履歴保存・UI 表示はフル履歴のまま維持する）。トリム不要ならそのまま返してよい。
    /// </summary>
    Conversation Fit(Conversation conversation);
}

/// <summary>トリムを行わない既定実装（テストや予算未設定時のフォールバック）。</summary>
public sealed class NoopContextWindowPolicy : IContextWindowPolicy
{
    public static readonly NoopContextWindowPolicy Instance = new();

    public Conversation Fit(Conversation conversation) => conversation;
}

/// <summary>切り詰め済みメッセージ列から送信用 <see cref="Conversation"/> を組み立てるヘルパ。</summary>
public static class ConversationView
{
    /// <summary><paramref name="messages"/> を内容に持つ新しい会話を作る（元会話とは別インスタンス）。</summary>
    public static Conversation FromMessages(IReadOnlyList<ChatMessage> messages)
    {
        var view = new Conversation();
        foreach (var m in messages)
            view.Messages.Add(m);
        return view;
    }
}
