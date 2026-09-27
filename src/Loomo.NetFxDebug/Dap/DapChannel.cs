using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sk0ya.Loomo.NetFxDebug.Dap;

/// <summary>
/// stdio 上の DAP（Debug Adapter Protocol）1 本。フレーミングは LSP と同じ <c>Content-Length</c> ヘッダー、
/// 本文は UTF-8 の JSON。読み取りは呼び出し元のスレッド（<see cref="DapServer"/> の受信ループ）だけが行い、
/// 書き込みはデバッグ対象のコールバックスレッドや出力の読み取りスレッドからも来るのでロックで直列化する。
/// </summary>
internal sealed class DapChannel
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 日本語のメッセージやパスを \uXXXX にしない（読めるログのため。DAP としてはどちらでも正しい）。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _writeLock = new();
    private int _seq;

    public DapChannel(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>次のメッセージを読む。ストリームの終端なら null。</summary>
    public JsonDocument? Read()
    {
        var contentLength = -1;
        while (true)
        {
            var line = ReadHeaderLine();
            if (line is null) return null;
            if (line.Length == 0)
            {
                if (contentLength >= 0) break;
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon > 0 && line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line.Substring(colon + 1).Trim());
        }

        var buffer = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = _input.Read(buffer, read, contentLength - read);
            if (n <= 0) return null;
            read += n;
        }
        return JsonDocument.Parse(buffer);
    }

    private string? ReadHeaderLine()
    {
        var bytes = new MemoryStream();
        while (true)
        {
            var b = _input.ReadByte();
            if (b < 0) return bytes.Length == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (b == '\n')
            {
                var text = Encoding.ASCII.GetString(bytes.ToArray());
                return text.TrimEnd('\r');
            }
            bytes.WriteByte((byte)b);
        }
    }

    public void SendResponse(int requestSeq, string command, bool success, object? body, string? message = null)
        => Write(new
        {
            seq = NextSeq(),
            type = "response",
            request_seq = requestSeq,
            success,
            command,
            message,
            body,
        });

    public void SendEvent(string @event, object? body = null)
        => Write(new { seq = NextSeq(), type = "event", @event, body });

    /// <summary>デバッグコンソールへ 1 行出す（<c>output</c> イベント）。</summary>
    public void Output(string text, string category = "console")
        => SendEvent("output", new { category, output = text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n" });

    private int NextSeq() => System.Threading.Interlocked.Increment(ref _seq);

    private void Write(object message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Options);
        var header = Encoding.ASCII.GetBytes("Content-Length: " + json.Length + "\r\n\r\n");
        lock (_writeLock)
        {
            try
            {
                _output.Write(header, 0, header.Length);
                _output.Write(json, 0, json.Length);
                _output.Flush();
            }
            catch (IOException) { /* クライアントが先に閉じた。終了処理は受信ループの EOF が担う */ }
            catch (ObjectDisposedException) { }
        }
    }
}
