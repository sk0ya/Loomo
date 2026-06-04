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

    /// <summary>
    /// アシスタントがツール呼び出しを「本文テキスト」として吐くモデル（例: qwen2.5-coder / phi4-mini）で、
    /// モデルが実際に生成した生本文を逐語で保持する。表示用の <see cref="Text"/> とは別物。
    /// 次ターンの送信時はこれを逐語でアシスタント発話として積み直す。Ollama のプレフィックスKV再利用は
    /// 「スロットの[プロンプト＋生成トークン]のバイト完全な拡張」のときだけ効くため、tool_calls へ
    /// 再構成すると食い違って会話全体が再 prefill される（turn2 が約8倍遅くなる）。null なら従来どおり。
    /// </summary>
    public string? ProviderContent { get; set; }

    /// <summary>このメッセージを生成した組み込みAIプロファイルID。ユーザー/ツールでは null。</summary>
    public string? AgentId { get; set; }

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
