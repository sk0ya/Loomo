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
/// sk0ya.Terminal.Controls 1.0.3 以降で <see cref="TerminalTabView"/> に
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

            // 端末がコマンドを「一度も実行できなかった」ときの保険。センチネル＝完了未検知
            // (Completed=false) かつ ExitCode=-1 かつ出力空。これはシェル統合が未確立／シェル種別
            // 不明／セッション未初期化など、可視ターミナルがまだ実行可能状態でないときに即座に返る値
            // （Terminal 側 ExecuteAgentCommandAsync）。この場合だけ独立プロセスで実行し直す。
            // キャンセル要求時はユーザー/ループが意図的に止めたので再実行しない。
            if (!tcr.Completed && tcr.ExitCode == -1 && string.IsNullOrEmpty(tcr.Output)
                && !ct.IsCancellationRequested)
            {
                var fallback = await RunViaProcessAsync(command, ct);
                CommandExecuted?.Invoke(this, fallback);
                return fallback;
            }

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

    /// <summary>可視ターミナルがまだ実行できない（起動直後でシェル統合未確立など）ときのフォールバック。
    /// 独立した PowerShell プロセスで**非対話**実行し、stdout/stderr/exit を取得する。
    /// 端末ペインには表示されない点に注意（あくまで端末未準備時の保険）。cwd は現在値を引き継ぐ。</summary>
    private async Task<CommandResult> RunViaProcessAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ShellExecutable,
            WorkingDirectory = Directory.Exists(_cwd) ? _cwd : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // -NoProfile: プロファイル読込を避け予測可能に。-NonInteractive: プロンプトで固まらない。
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var sync = new object(); // OutputDataReceived/ErrorDataReceived はスレッドプールで発火するため保護。
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sync) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sync) sb.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new CommandResult(command, $"フォールバック実行に失敗しました: {ex.Message}", -1, _cwd, false);
        }

        proc.StandardInput.Close(); // 標準入力を即 EOF にしてプロンプト/標準入力待ちを防ぐ。
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 万一プロセスがパイプを握ったまま終了しない事態に備えた安全網（実体 exe 起動なら通常到達しない）。
        using var timeout = new CancellationTokenSource(FallbackTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            lock (sync) return new CommandResult(command, sb.ToString(), -1, _cwd, false);
        }

        TrackChdir(command);
        var exit = proc.ExitCode;
        lock (sync) return new CommandResult(command, sb.ToString(), exit, _cwd, exit == 0);
    }

    /// <summary>フォールバック実行で使う PowerShell 実行ファイル。
    /// <b>WindowsApps（ストア版パッケージ/実行エイリアス）の pwsh は避ける</b>：
    /// UseShellExecute=false＋出力リダイレクトで起動すると再起動ブローカーがパイプを握り、
    /// WaitForExit が返らずハングするため。通常版 pwsh → 非WindowsApps の PATH 上 pwsh →
    /// System32 の Windows PowerShell、の順に実体のある exe を選ぶ。</summary>
    private static readonly string ShellExecutable = ResolveShellExecutable();

    /// <summary>フォールバック実行の安全網タイムアウト。通常は到達しない（ハング保険）。</summary>
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromMinutes(2);

    private static string ResolveShellExecutable()
    {
        // 1) 通常インストールの pwsh (PowerShell 7+)。
        var pf = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(pf))
        {
            var pwsh7 = Path.Combine(pf, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(pwsh7)) return pwsh7;
        }

        // 2) PATH 上の pwsh.exe。ただし WindowsApps（ストア版）は除外。
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 不正な PATH 要素は無視 */ }
        }

        // 3) 最後の砦：Windows PowerShell（System32、必ず実体があり GUI からの起動も安定）。
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var winPosh = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(winPosh) ? winPosh : "powershell.exe";
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* 既に終了/権限不足など。無視 */ }
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
