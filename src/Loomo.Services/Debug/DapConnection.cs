using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>
/// <see cref="Stream"/> 上で DAP（Debug Adapter Protocol）メッセージをやり取りする 1 本の接続。
/// トランスポート（stdio のプロセス／TCP ソケット）から独立しており、<see cref="DapProtocolClient"/>（stdio）と
/// js-debug 系の TCP 接続の両方がこれを合成する。フレーミングは LSP と同じ <c>Content-Length</c>、
/// 本文は DAP の <c>{"seq":N,"type":"request|response|event",...}</c>。
///
/// - <b>request</b>（client→adapter）：<see cref="SendRequestAsync"/> が <c>request_seq</c> で response と突き合わせる。
/// - <b>event</b>（adapter→client）：<see cref="EventReceived"/> を発火（output / stopped / terminated / exited 等）。
/// - <b>reverse request</b>（adapter→client、例: runInTerminal / startDebugging）：<see cref="ReverseRequestHandler"/>
///   が設定されていればそれを呼び、戻り値を success として応答する。未設定なら従来どおり無条件で success 応答
///   （netcoredbg 経路の挙動を変えないための既定）。
///
/// ストリームの寿命は所有しない（stdio ならプロセス、TCP ならソケットの所有者が閉じる）。読み取りスレッドは
/// <see cref="Start"/> で開始する — reverse request を扱う側はハンドラ設定後に呼ぶことで取りこぼしを防ぐ。
/// </summary>
internal sealed class DapConnection : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly string _label;
    private readonly object _writeLock = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private int _nextSeq;
    private bool _disposed;
    private bool _started;

    /// <summary>アダプタからの event。引数は (event 名, body)。body が無ければ <see cref="JsonValueKind.Undefined"/>。</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>読み取りループが終わったとき（ストリーム EOF / IO エラー）に発火。Dispose 起因では発火しない。</summary>
    public event Action? Closed;

    /// <summary>reverse request のハンドラ。引数は (command, arguments)。戻り値が応答の success になる。
    /// 読み取りスレッド上で呼ばれるためブロックしないこと（重い処理は Task へ逃がす）。null なら無条件 success。</summary>
    public Func<string, JsonElement, bool>? ReverseRequestHandler { get; set; }

    public bool IsOpen => !_disposed;

    /// <param name="input">アダプタ→クライアント方向のストリーム。</param>
    /// <param name="output">クライアント→アダプタ方向のストリーム（stdio では input と別、TCP では同一で可）。</param>
    /// <param name="label">ログの接頭辞（親/子接続の区別用）。</param>
    public DapConnection(Stream input, Stream output, string label = "dap")
    {
        _input = input;
        _output = output;
        _label = label;
    }

    /// <summary>読み取りスレッドを開始する。多重呼び出しは無視。</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        var thread = new Thread(ReadLoop) { IsBackground = true, Name = $"DapRead:{_label}" };
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
        Log($"{_label} send: {(json.Length > 300 ? json[..300] + "…" : json)}");
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_writeLock)
        {
            _output.Write(header);
            _output.Write(body);
            _output.Flush();
        }
    }

    private void ReadLoop()
    {
        while (!_disposed)
        {
            int len;
            byte[]? body;
            try
            {
                len = ReadContentLength(_input);
                if (len <= 0) break;   // EOF
                body = ReadExact(_input, len);
                if (body == null) break; // EOF
            }
            catch { break; }  // 実 IO エラー — 読み取り停止

            try
            {
                var json = Encoding.UTF8.GetString(body);
                Log($"{_label} recv: {(json.Length > 300 ? json[..300] + "…" : json)}");
                HandleMessage(json);
            }
            catch { }  // 不正 JSON / ディスパッチ失敗はこのメッセージだけ読み飛ばす
        }
        Log($"{_label} ReadLoop exited");

        // 残った pending をキャンセルし、終了を通知。
        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
        if (!_disposed) Closed?.Invoke();
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
                // アダプタ起点のリクエスト（reverse request）。ハンドラがあれば委ね、無ければ
                // ブロックさせないよう success 応答を返す（従来挙動）。
                if (root.TryGetProperty("seq", out var sq) && sq.ValueKind == JsonValueKind.Number)
                {
                    var cmd = root.TryGetProperty("command", out var c) ? c.GetString() : null;
                    bool success = true;
                    if (ReverseRequestHandler is { } handler && cmd is not null)
                    {
                        var args = root.TryGetProperty("arguments", out var a) ? a : default;
                        try { success = handler(cmd, args); }
                        catch { success = false; }
                    }
                    SendReverseResponse(sq.GetInt32(), cmd, success);
                }
                break;
            }
        }
    }

    private void SendReverseResponse(int requestSeq, string? command, bool success)
    {
        int seq = Interlocked.Increment(ref _nextSeq);
        try
        {
            WriteMessage(JsonSerializer.Serialize(new
            {
                seq,
                type = "response",
                request_seq = requestSeq,
                success,
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
        lock (_pending)
        {
            foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
            _pending.Clear();
        }
    }
}
