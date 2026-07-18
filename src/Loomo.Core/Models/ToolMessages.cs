namespace sk0ya.Loomo.Core.Models;

/// <summary>アシスタントからのツール呼び出し要求。</summary>
public sealed record ToolUse(string Id, string Name, string ArgumentsJson, string? RawJson = null);

/// <summary>会話に戻すツール実行結果。</summary>
public sealed record ToolResultMessage(string ToolUseId, string Content, bool IsError);
