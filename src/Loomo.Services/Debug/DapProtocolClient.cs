using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>
/// デバッグアダプタ（DAP: Debug Adapter Protocol）プロセスを起動し、stdio 上で DAP メッセージをやり取りする。
/// フレーミング/リクエスト突合/イベントディスパッチは <see cref="DapConnection"/> に委譲し、本クラスは
/// プロセスの寿命（起動・強制終了）だけを持つ薄いラッパ。TCP 系アダプタ（js-debug）は本クラスを使わず
/// <see cref="DapConnection"/> を直接合成する。
/// </summary>
internal sealed class DapProtocolClient : IDisposable
{
    private readonly Process _process;
    private readonly DapConnection _connection;
    private bool _disposed;

    /// <summary>アダプタからの event。引数は (event 名, body)。body が無ければ <see cref="JsonValueKind.Undefined"/>。</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>アダプタプロセスが終了したとき（stdout EOF）に発火。</summary>
    public event Action? Exited;

    public bool IsRunning => !_disposed && !_process.HasExited;

    public DapProtocolClient(string executable, IEnumerable<string> args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        _process = Process.Start(psi) ?? throw new InvalidOperationException("デバッグアダプタの起動に失敗しました");

        _connection = new DapConnection(
            _process.StandardOutput.BaseStream,
            _process.StandardInput.BaseStream,
            label: "stdio");
        _connection.EventReceived += (evt, body) => EventReceived?.Invoke(evt, body);
        _connection.Closed += () => { if (!_disposed) Exited?.Invoke(); };
        _connection.Start();
    }

    /// <summary>request を送り、対応する response の body（失敗時は例外）を待つ。</summary>
    public Task<JsonElement?> SendRequestAsync(string command, object? arguments, CancellationToken ct = default)
        => _connection.SendRequestAsync(command, arguments, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
    }
}
