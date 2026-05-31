using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using Terminal.Tabs;

namespace sk0ya.Loomo.Services;

/// <summary>
/// ITerminalService 実装。
///
/// sk0ya.Terminal.Controls 1.0.3 で <see cref="TerminalTabView"/> に
/// <see cref="TerminalTabView.RunCommandAsync"/>（コマンド実行＋stdout/exit取得）が
/// 追加されたため、エージェントの実行を**可視ターミナルへ一本化**する。
/// 以前のように独立した PowerShell <c>Process</c> を裏で起動する必要はなく、
/// AI が打ったコマンドも人間が打ったコマンドと同じターミナルに表示・記録される。
/// </summary>
public sealed class TerminalService : ITerminalService
{
    private TerminalTabView? _view;
    private string _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public event EventHandler<CommandResult>? CommandExecuted;

    public string CurrentDirectory => _cwd;
    public bool IsExecuting { get; private set; }

    /// <summary>可視ターミナルを結びつける（コマンド実行・フォーカス等の操作用）。</summary>
    public void Attach(TerminalTabView view)
    {
        _view = view;
        if (Directory.Exists(view.WorkingDirectory))
            _cwd = view.WorkingDirectory;
    }

    public void SetWorkingDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        var changed = !string.Equals(_cwd, path, StringComparison.OrdinalIgnoreCase);
        _cwd = path;

        // 可視ターミナルにも追従させる（人間が cd するのと同じ操作）。
        // 起動直後の同一ディレクトリ設定では余計な cd を打たない。
        if (changed && _view is { } view)
            _ = view.Dispatcher.InvokeAsync(
                () => view.RunCommandAsync($"cd \"{path}\"", CancellationToken.None));
    }

    public async Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
    {
        var view = _view
            ?? throw new InvalidOperationException(
                "可視ターミナルが未アタッチです。ShellWindow で Attach を呼んでください。");

        IsExecuting = true;
        try
        {
            // TerminalTabView は WPF コントロールなので UI スレッドで実行する。
            var tcr = await view.Dispatcher
                .InvokeAsync(() => view.RunCommandAsync(command, ct))
                .Task.Unwrap();

            TrackChdir(command);
            // shell integration が有効なら、実際の cwd をターミナルから取得して同期する。
            if (view.IsShellIntegrationActive && Directory.Exists(view.WorkingDirectory))
                _cwd = view.WorkingDirectory;

            var success = tcr.Completed && tcr.ExitCode == 0;
            var result = new CommandResult(command, tcr.Output, tcr.ExitCode, _cwd, success);
            CommandExecuted?.Invoke(this, result);
            return result;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    /// <summary>shell integration が無い場合のフォールバックとして `cd &lt;dir&gt;` を検知して cwd を追従。</summary>
    private void TrackChdir(string command)
    {
        var trimmed = command.Trim();
        if (!trimmed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase)) return;
        var target = trimmed[3..].Trim().Trim('"');
        var resolved = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(_cwd, target));
        if (Directory.Exists(resolved)) _cwd = resolved;
    }
}
