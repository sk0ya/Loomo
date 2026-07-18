namespace sk0ya.Loomo.Core.Models;

/// <summary>会話のロール。</summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>会話の1メッセージ。</summary>
public sealed class ChatMessage
{
    public ChatRole Role { get; init; }
    public string? Text { get; set; }
    /// <summary>このメッセージを生成した組み込みAIプロファイルID。</summary>
    public string? AgentId { get; set; }
    /// <summary>ターンの進行状況を保存するUI診断用メタデータ。</summary>
    public string? ProgressLog { get; set; }
    /// <summary>このターン限定でユーザー本文の直前に差し込む追加プロンプト。</summary>
    public string? RenderPrefix { get; set; }
    /// <summary>アシスタントが要求したツール呼び出し。</summary>
    public List<ToolUse> ToolUses { get; } = new();
    /// <summary>ツール結果。</summary>
    public List<ToolResultMessage> ToolResults { get; } = new();
}

/// <summary>会話全体。</summary>
public sealed class Conversation
{
    public List<ChatMessage> Messages { get; } = new();

    public ChatMessage AddUser(string text)
    {
        var message = new ChatMessage { Role = ChatRole.User, Text = text };
        Messages.Add(message);
        return message;
    }
}
