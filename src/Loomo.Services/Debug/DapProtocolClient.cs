using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>
/// デバッグアダプタ（DAP: Debug Adapter Protocol）プロセスを起動し、stdio 上で DAP メッセージをやり取りする。
/// 枠組みはエディタの LSP <c>LspProcess</c> と同じ <c>Content-Length</c> フレーミングだが、本文の形が異なる：
/// DAP は <c>{"seq":N,"type":"request|response|event",...}</c> を使う。
///
/// - <b>request</b>（client→adapter）：<see cref="SendRequestAsync"/> が <c>request_seq</c> で response と突き合わせる。
/// - <b>event</b>（adapter→client）：<see cref="EventReceived"/> を発火（output / stopped / terminated / exited 等）。
/// - <b>reverse request</b>（adapter→client、例: runInTerminal）：ブロックさせないよう success 応答を返す。
/// </summary>
internal sealed class DapProtocolClient : IDisposable
{
    private readonly Process _process;
    private readonly object _writeLock = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private int _nextSeq;
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

        var thread = new Thread(ReadLoop) { IsBackground = true, Name = "DapStdout" };
        thread.Start();
    }

    /// <summary>request を送り、対応する response の body（失敗時は例外）を待つ。</summary>
    public Task<JsonElement?> SendRequestAsync(string command, object? arguments, CancellationToken ct = default)
    {
        int seq = Interlocked.Increment(ref _nextSeq);
        var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[seq] = tcs;

        try
        {
            WriteMessage(JsonSerializer.Serialize(new
            {
                seq,
                type = "request",
                command,
                arguments,
            }));
        }
        catch (Exception ex)
        {
            lock (_pending) _pending.Remove(seq);
            tcs.SetException(ex);
            return tcs.Task;
        }

        if (ct.CanBeCanceled)
            ct.Register(() => { lock (_pending) _pending.Remove(seq); tcs.TrySetCanceled(ct); });

        return tcs.Task;
    }

    private void WriteMessage(string json)
    {
        Log($"send: {(json.Length > 300 ? json[..300] + "…" : json)}");
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_writeLock)
        {
            var stream = _process.StandardInput.BaseStream;
            stream.Write(header);
            stream.Write(body);
            stream.Flush();
        }
    }

    private void ReadLoop()
    {
        var stream = _process.StandardOutput.BaseStream;
        while (!_disposed)
        {
            int len;
            byte[]? body;
            try
            {
                len = ReadContentLength(stream);
                if (len <= 0) break;   // EOF
                body = ReadExact(stream, len);
                if (body == null) break; // EOF
            }
            catch { break; }  // 実 IO エラー — 読み取り停止

            try
            {
                var json = Encoding.UTF8.GetString(body);
                Log($"recv: {(json.Length > 300 ? json[..300] + "…" : json)}");
                HandleMessage(json);
            }
            catch { }  // 不正 JSON / ディスパッチ失敗はこのメッセージだけ読み飛ばす
        }
        Log("ReadLoop exited");

        // 残った pending をキャンセルし、終了を通知。
        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
        if (!_disposed) Exited?.Invoke();
    }

    private static int ReadContentLength(Stream stream)
    {
        int contentLength = -1;
        while (true)
        {
            var line = ReadLine(stream);
            if (line == null) return -1;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out int len))
                contentLength = len;
            if (line.Length == 0) return contentLength;
        }
    }

    private static string? ReadLine(Stream stream)
    {
        var bytes = new List<byte>(128);
        while (true)
        {
            int b = stream.ReadByte();
            if (b == -1) return null;
            if (b == '\r')
            {
                int next = stream.ReadByte();
                if (next == -1) return null;
                if (next == '\n') break;
                bytes.Add((byte)b);
                bytes.Add((byte)next);
            }
            else if (b == '\n') break;
            else bytes.Add((byte)b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static byte[]? ReadExact(Stream stream, int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buf, read, count - read);
            if (n == 0) return null;
            read += n;
        }
        return buf;
    }

    private void HandleMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "response":
            {
                int reqSeq = root.TryGetProperty("request_seq", out var rs) && rs.ValueKind == JsonValueKind.Number
                    ? rs.GetInt32() : -1;
                bool success = !root.TryGetProperty("success", out var su) || su.ValueKind != JsonValueKind.False;

                TaskCompletionSource<JsonElement?>? tcs;
                lock (_pending) { _pending.TryGetValue(reqSeq, out tcs); _pending.Remove(reqSeq); }
                if (tcs is null) break;

                if (success)
                    tcs.TrySetResult(root.TryGetProperty("body", out var b) ? b : default);
                else
                {
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                    var cmd = root.TryGetProperty("command", out var c) ? c.GetString() : "?";
                    tcs.TrySetException(new InvalidOperationException(
                        $"DAP リクエスト '{cmd}' が失敗しました: {msg ?? "(理由不明)"}"));
                }
                break;
            }
            case "event":
            {
                var evt = root.TryGetProperty("event", out var e) ? e.GetString() : null;
                if (evt is null) break;
                var body = root.TryGetProperty("body", out var b) ? b : default;
                EventReceived?.Invoke(evt, body);
                break;
            }
            case "request":
            {
                // アダプタ起点のリクエスト（reverse request）。ブロックさせないよう success 応答を返す。
                if (root.TryGetProperty("seq", out var sq) && sq.ValueKind == JsonValueKind.Number)
                {
                    var cmd = root.TryGetProperty("command", out var c) ? c.GetString() : null;
                    SendReverseResponse(sq.GetInt32(), cmd);
                }
                break;
            }
        }
    }

    private void SendReverseResponse(int requestSeq, string? command)
    {
        int seq = Interlocked.Increment(ref _nextSeq);
        try
        {
            WriteMessage(JsonSerializer.Serialize(new
            {
                seq,
                type = "response",
                request_seq = requestSeq,
                success = true,
                command,
                body = (object?)null,
            }));
        }
        catch { }
    }

    private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "loomo-dap-debug.log");
    private static void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        _process.Dispose();
        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
    }
}
