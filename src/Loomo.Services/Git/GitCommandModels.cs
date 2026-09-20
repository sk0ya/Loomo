namespace sk0ya.Loomo.Services;

/// <summary>変更系 git コマンド実行の通知。</summary>
public sealed record GitOperationEventArgs(string Command, bool Success);

/// <summary>git コマンド1回の実行結果。</summary>
public sealed record GitCommandResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;

    /// <summary>git はエラーや進捗を stderr へ出すため、UI 表示では stderr を優先する。</summary>
    public string Message
    {
        get
        {
            var error = Error.Trim();
            return error.Length > 0 ? error : Output.Trim();
        }
    }
}
