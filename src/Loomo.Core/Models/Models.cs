using System.Collections.Generic;

namespace sk0ya.Loomo.Core.Models;

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
    Local
}

/// <summary>UIのカラーテーマ（配色）。各値は <c>Themes/Palette.&lt;name&gt;.xaml</c> に一対一で対応する。</summary>
public enum AppTheme
{
    /// <summary>VS Code Dark 系（既定）。</summary>
    Dark,
    /// <summary>明るい配色。</summary>
    Light,
    /// <summary>Solarized Dark 系。</summary>
    SolarizedDark,
    /// <summary>Nord 系（青みがかった暗色）。</summary>
    Nord,
    /// <summary>高コントラスト（暗）。</summary>
    HighContrast
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

    /// <summary>このメッセージを生成した組み込みAIプロファイルID。ユーザー/ツールでは null。</summary>
    public string? AgentId { get; set; }

    /// <summary>このメッセージで始まったターンの「進行状況」ログ（実行構成・AI内訳・各イベントの経過時間）。
    /// AIコンテキストには使わないUI診断用メタデータで、user メッセージにのみ付く。セッション復元時に
    /// 進捗表示を再構築するため永続化する（プロンプト整形系はこのフィールドを読まない）。</summary>
    public string? ProgressLog { get; set; }

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
public sealed record ToolUse(string Id, string Name, string ArgumentsJson, string? RawJson = null);

/// <summary>ツール実行結果（会話に戻す用）。</summary>
public sealed record ToolResultMessage(string ToolUseId, string Content, bool IsError);
