using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Core.Abstractions;

/// <summary>ターミナル（sk0ya.Terminal）への操作を抽象化。</summary>
public interface ITerminalService
{
    /// <summary>コマンドを実行し結果を待つ。</summary>
    Task<CommandResult> RunCommandAsync(string command, CancellationToken ct);
    /// <summary>コマンドを可視ターミナルの PTY で実行し、画面表示と結果取得を両立する。
    /// 未接続時の既定実装は通常の非表示実行へフォールバックする。</summary>
    Task<CommandResult> RunCommandInVisibleTerminalAsync(string command, CancellationToken ct)
        => RunCommandAsync(command, ct);
    /// <summary>作業ディレクトリを設定。</summary>
    void SetWorkingDirectory(string path);
    /// <summary>コマンドを可視ターミナルへ送って実行する。未接続、または前面で別のプログラム
    /// （実行中のコマンドや Claude Code）が動いていて打ち込めないなら false。</summary>
    bool TryRunInVisibleTerminal(string command);
    /// <summary>終わらないコマンド（watch）を可視ターミナルへ打ち込み、その出力を論理行ごとに追う。
    /// <paramref name="lineReceived"/> は確定した行ごと、<paramref name="commandFinished"/> はそのコマンドが終わったとき
    /// （終了コード。セッションが消えたら null）に1度だけ、どちらも UI スレッドで呼ばれる。Dispose で追うのをやめる
    /// （コマンドは止めない）。未接続なら null。<see cref="RunCommandInVisibleTerminalAsync"/> と違って
    /// 終わりを待つ列を占有しないので、watch が走っている間も他の呼び出しは詰まらない。</summary>
    IDisposable? StartWatchedInVisibleTerminal(string command, Action<string> lineReceived, Action<int?> commandFinished)
        => null;
    string CurrentDirectory { get; }
    bool IsExecuting { get; }
    /// <summary>実行された全コマンド結果（人間・AI問わず）の通知。</summary>
    event EventHandler<CommandResult>? CommandExecuted;
}
