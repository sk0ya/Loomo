using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using Terminal.Tabs;

namespace sk0ya.Loomo.Services;

/// <summary>
/// ITerminalService 実装。
///
/// sk0ya の <see cref="TerminalTabView"/> は「対話型ターミナル」であり、コマンドを
/// プログラムから実行して出力を取得する公開APIを持たない（人間が打ち込む用）。
/// そこでエージェントの実行バックエンドは独立した <see cref="Process"/>（PowerShell）とし、
/// 可視ターミナルは人間の操作用として併存させる。
/// （将来 TerminalTabView 側に「コマンド実行＋出力取得」APIを追加すれば一本化可能。）
/// </summary>
public sealed class TerminalService : ITerminalService
{
    private TerminalTabView? _view;
    private string _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public event EventHandler<CommandResult>? CommandExecuted;

    public string CurrentDirectory => _cwd;
    public bool IsExecuting { get; private set; }

    /// <summary>可視ターミナルを結びつける（フォーカス等の操作用）。</summary>
    public void Attach(TerminalTabView view) => _view = view;

    public void SetWorkingDirectory(string path)
    {
        if (Directory.Exists(path)) _cwd = path;
    }

    public async Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
    {
        IsExecuting = true;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = _cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);

            using var proc = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            TrackChdir(command);
            var result = new CommandResult(command, sb.ToString(), proc.ExitCode, _cwd, proc.ExitCode == 0);
            CommandExecuted?.Invoke(this, result);
            return result;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    /// <summary>`cd &lt;dir&gt;` を検知して作業ディレクトリを追従（プロセスは都度生成のため）。</summary>
    private void TrackChdir(string command)
    {
        var trimmed = command.Trim();
        if (!trimmed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase)) return;
        var target = trimmed[3..].Trim().Trim('"');
        var resolved = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(_cwd, target));
        if (Directory.Exists(resolved)) _cwd = resolved;
    }
}
