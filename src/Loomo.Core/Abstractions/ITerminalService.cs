using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>ターミナル（sk0ya.Terminal）への操作を抽象化。</summary>
public interface ITerminalService
{
    /// <summary>コマンドを実行し結果を待つ。</summary>
    Task<CommandResult> RunCommandAsync(string command, CancellationToken ct);
    /// <summary>作業ディレクトリを設定。</summary>
    void SetWorkingDirectory(string path);
    /// <summary>コマンドを可視ターミナルへ送って実行する。未接続なら false。</summary>
    bool TryRunInVisibleTerminal(string command);
    string CurrentDirectory { get; }
    bool IsExecuting { get; }
    /// <summary>実行された全コマンド結果（人間・AI問わず）の通知。</summary>
    event EventHandler<CommandResult>? CommandExecuted;
}
