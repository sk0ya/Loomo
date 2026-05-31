using System.Collections.Generic;

namespace AgentStudio.Core.Models;

/// <summary>ターミナルでのコマンド実行結果。</summary>
public sealed record CommandResult(
    string Command,
    string Output,
    int ExitCode,
    string WorkingDirectory,
    bool Success);

/// <summary>FolderTree 上のファイル/フォルダノード。</summary>
public sealed record FileNode(string Name, string FullPath, bool IsDirectory);

/// <summary>AIプロバイダ種別。</summary>
public enum AiProvider
{
    Stub,
    Claude,
    OpenAI,
    Copilot,
    Local
}

/// <summary>会話のロール。</summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>1メッセージ。アシスタントのツール呼び出し / ツール結果も保持する。</summary>
public sealed class ChatMessage
{
    public ChatRole Role { get; init; }
    public string? Text { get; set; }

    /// <summary>アシスタントが要求したツール呼び出し（複数可）。</summary>
    public List<ToolUse> ToolUses { get; } = new();

    /// <summary>ツール結果（Role == Tool のとき）。</summary>
    public List<ToolResultMessage> ToolResults { get; } = new();
}

/// <summary>会話全体。</summary>
public sealed class Conversation
{
    public List<ChatMessage> Messages { get; } = new();

    public ChatMessage AddUser(string text)
    {
        var m = new ChatMessage { Role = ChatRole.User, Text = text };
        Messages.Add(m);
        return m;
    }
}

/// <summary>アシスタントからのツール呼び出し要求。</summary>
public sealed record ToolUse(string Id, string Name, string ArgumentsJson);

/// <summary>ツール実行結果（会話に戻す用）。</summary>
public sealed record ToolResultMessage(string ToolUseId, string Content, bool IsError);
