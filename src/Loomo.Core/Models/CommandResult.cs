namespace sk0ya.Loomo.Core.Models;

/// <summary>ターミナルでのコマンド実行結果。</summary>
public sealed record CommandResult(
    string Command,
    string Output,
    int ExitCode,
    string WorkingDirectory,
    bool Success);
