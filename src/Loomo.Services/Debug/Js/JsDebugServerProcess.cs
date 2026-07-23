using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services.Debug.Js;

/// <summary>
/// vscode-js-debug の DAP サーバプロセス（<c>node dapDebugServer.js 0 127.0.0.1</c>）を 1 つ起動して保持する。
/// ポートに 0 を渡して OS に割り当てさせ、stdout の「Debug server listening at 127.0.0.1:PORT」行から
/// 実ポートをパースする（固定ポートの競合レースを避ける）。1 デバッグセッションにつき 1 プロセス。
/// このプロセスへは親セッションと（startDebugging ごとの）子セッションが別々の TCP 接続を張る。
/// </summary>
internal sealed partial class JsDebugServerProcess : IDisposable
{
    [GeneratedRegex(@"listening\s+at\s+\S*?:(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ListeningLine();

    private readonly Process _process;
    private bool _disposed;

    /// <summary>サーバが待ち受けている TCP ポート。</summary>
    public int Port { get; }

    public bool IsRunning => !_disposed && !_process.HasExited;

    private JsDebugServerProcess(Process process, int port)
    {
        _process = process;
        Port = port;
    }

    /// <summary>サーバを起動し、待ち受けポートの表示まで待つ（既定 10 秒でタイムアウト）。</summary>
    public static async Task<JsDebugServerProcess> StartAsync(string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDir,
        };
        psi.ArgumentList.Add(JsDebugAdapterLocator.DapServerPath);
        psi.ArgumentList.Add("0");            // ポートは OS 割り当て（listening 行から読む）
        psi.ArgumentList.Add("127.0.0.1");

        var process = Process.Start(psi) ?? throw new InvalidOperationException("js-debug サーバの起動に失敗しました");
        try
        {
            var portTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var m = ListeningLine().Match(e.Data);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var p)) portTcs.TrySetResult(p);
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
            process.Exited += (_, _) =>
            {
                string err;
                lock (stderr) err = stderr.ToString();
                portTcs.TrySetException(new InvalidOperationException(
                    $"js-debug サーバが起動直後に終了しました。{(err.Length > 0 ? $" stderr: {err}" : "")}"));
            };
            process.EnableRaisingEvents = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            int port;
            try
            {
                port = await portTcs.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("js-debug サーバの待ち受けポート表示がタイムアウトしました。");
            }
            return new JsDebugServerProcess(process, port);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
    }
}
