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
/// エージェント（AI）のコマンド実行は**可視ターミナルへは流さず**、独立した非対話
/// PowerShell <c>Process</c> を裏で起動して stdout/exit を取得する。AI の実行ログで
/// 人間のターミナルが汚れたり操作が混ざったりしないようにするため。可視ターミナルは
/// 人間専用で、<see cref="SetWorkingDirectory"/> によるフォルダ追従（cd 反映）にのみ用いる。
/// （以前は <see cref="TerminalTabView.RunCommandAsync"/> へ一本化していたが、AI 実行を
/// ターミナルに流すのをやめてこの方式に戻した。）
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

    /// <summary>可視ターミナルへコマンドを送って人間と同じ経路で実行する（インストール等、出力を見せたい操作用）。
    /// 端末が未接続なら false。<see cref="RunCommandAsync"/>（裏の非対話プロセス）とは別経路。</summary>
    public bool TryRunInVisibleTerminal(string command)
    {
        if (_view is not { } view) return false;
        view.Dispatcher.InvokeAsync(() => view.RunCommandAsync(command, CancellationToken.None));
        return true;
    }

    public async Task<CommandResult> RunCommandAsync(string command, CancellationToken ct)
    {
        IsExecuting = true;
        try
        {
            // AI の実行は可視ターミナルに流さず、常に独立した非対話プロセスで実行する。
            var result = await RunViaProcessAsync(command, ct);
            CommandExecuted?.Invoke(this, result);
            return result;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    /// <summary>独立した PowerShell プロセスで**非対話**実行し、stdout/stderr/exit を取得する。
    /// 端末ペインには表示されない（AI の実行は人間のターミナルに流さない）。cwd は現在値を引き継ぎ、
    /// <c>cd</c> は <see cref="TrackChdir"/> で追従する。</summary>
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
        // -ExecutionPolicy Bypass: システムポリシーが Restricted/AllSigned だと Node 付属の
        // npx.ps1 / npm.ps1（未署名シム）が「デジタル署名されていません」で実行不能になり、
        // 型チェック（npx tsc）や vitest/jest 実行が全滅するため。実行ポリシーはセキュリティ境界では
        // なく（コマンド安全性は BlockedCommandPatterns 側が担う）、非対話ランナーの互換性を優先する。
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(Utf8Preamble + command);

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

    /// <summary>子 PowerShell の入出力を UTF-8 に固定するプリアンブル（コマンドの前置句）。
    /// 既定では子シェルがネイティブツール（rg/git 等）の UTF-8 出力を
    /// <c>[Console]::OutputEncoding</c>（日本語環境では cp932）で誤デコードし、特に stderr の
    /// エラー文が文字化けして AI が失敗理由を読めず自己修正できなくなる。
    /// <c>[Console]::OutputEncoding</c> はネイティブ出力のデコードと子シェル自身の stdout
    /// エンコードの両方を司り、呼び出し側の <c>StandardOutputEncoding=UTF8</c> と整合する。
    /// <c>$OutputEncoding</c> はネイティブコマンドへパイプ入力する際のエンコード（BOM なし UTF-8）。
    ///
    /// <c>$PSDefaultParameterValues['*:Encoding']='utf8'</c> は PowerShell の**ファイル読み書き
    /// cmdlet**（Get-Content / Select-String / Set-Content / Out-File 等）の既定エンコードを UTF-8 に
    /// 揃える。これらは <c>[Console]::OutputEncoding</c> とは別系統で、pwsh 7 未導入で Windows
    /// PowerShell 5.1 にフォールバックすると既定がシステム ANSI（日本語環境では cp932）になり、
    /// UTF-8 のソースを <c>Get-Content</c> すると文字化けする（pwsh 7 は既定 UTF-8 なので顕在化しない）。
    /// pwsh 7 では 'utf8' は BOM なし、WinPS 5.1 では BOM 付きになるが、化けるよりは可読性を優先する
    /// （エージェントのファイル作成は主に write_file/edit_file 経由でこの経路を通らない）。
    /// 設定に失敗してもコマンド実行自体は続行する（try/catch）。
    /// ハーネス（AgentCapabilityHarness）はこの TerminalService をそのまま使うので挙動が揃う。</summary>
    private const string Utf8Preamble =
        "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
        "$OutputEncoding = New-Object System.Text.UTF8Encoding $false; " +
        "$PSDefaultParameterValues['*:Encoding'] = 'utf8' } catch { }; ";

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
