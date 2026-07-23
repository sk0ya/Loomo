using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Services.Debug;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>Stream 上の DAP 接続（<see cref="DapConnection"/>）のフレーミング・リクエスト突合・イベント・
/// reverse request 応答のテスト。匿名パイプ 2 対（アダプタ→クライアント／クライアント→アダプタ）の上で
/// 偽アダプタを手書きして検証する。</summary>
public sealed class DapConnectionTests : IDisposable
{
    // アダプタ→クライアント（クライアントの input）
    private readonly AnonymousPipeServerStream _adapterOut = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _clientIn;
    // クライアント→アダプタ（クライアントの output）
    private readonly AnonymousPipeServerStream _adapterIn = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _clientOut;
    private readonly DapConnection _conn;

    public DapConnectionTests()
    {
        _clientIn = new AnonymousPipeClientStream(PipeDirection.In, _adapterOut.ClientSafePipeHandle);
        _clientOut = new AnonymousPipeClientStream(PipeDirection.Out, _adapterIn.ClientSafePipeHandle);
        _conn = new DapConnection(_clientIn, _clientOut, label: "test");
    }

    public void Dispose()
    {
        _conn.Dispose();
        _adapterOut.Dispose();
        _adapterIn.Dispose();
        _clientIn.Dispose();
        _clientOut.Dispose();
    }

    /// <summary>偽アダプタ側：1 メッセージを Content-Length フレーミングで送る。</summary>
    private void AdapterSend(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        _adapterOut.Write(header);
        _adapterOut.Write(body);
        _adapterOut.Flush();
    }

    /// <summary>偽アダプタ側：1 メッセージを読んでパースする（ヘッダ→本文）。</summary>
    private JsonDocument AdapterReceive()
    {
        int len = -1;
        while (true)
        {
            var line = ReadLine(_adapterIn);
            Assert.NotNull(line);
            if (line!.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                len = int.Parse(line["Content-Length:".Length..].Trim());
            if (line.Length == 0) break;
        }
        Assert.True(len > 0);
        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = _adapterIn.Read(buf, read, len - read);
            Assert.True(n > 0);
            read += n;
        }
        return JsonDocument.Parse(buf);
    }

    private static string? ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = s.ReadByte();
            if (b == -1) return null;
            if (b == '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Request_gets_matched_to_response_by_seq()
    {
        _conn.Start();
        var task = _conn.SendRequestAsync("initialize", new { adapterID = "x" });

        using var req = AdapterReceive();
        var seq = req.RootElement.GetProperty("seq").GetInt32();
        Assert.Equal("request", req.RootElement.GetProperty("type").GetString());
        Assert.Equal("initialize", req.RootElement.GetProperty("command").GetString());

        AdapterSend($$$"""{"seq":1,"type":"response","request_seq":{{{seq}}},"success":true,"command":"initialize","body":{"supportsSetVariable":true}}""");

        var body = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(body!.Value.GetProperty("supportsSetVariable").GetBoolean());
    }

    [Fact]
    public async Task Failed_response_throws_with_message()
    {
        _conn.Start();
        var task = _conn.SendRequestAsync("launch", new { });
        using var req = AdapterReceive();
        var seq = req.RootElement.GetProperty("seq").GetInt32();

        AdapterSend($$$"""{"seq":1,"type":"response","request_seq":{{{seq}}},"success":false,"command":"launch","message":"boom"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task Events_are_dispatched_with_name_and_body()
    {
        var tcs = new TaskCompletionSource<(string, JsonElement)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _conn.EventReceived += (evt, body) => tcs.TrySetResult((evt, body));
        _conn.Start();

        AdapterSend("""{"seq":1,"type":"event","event":"stopped","body":{"reason":"breakpoint","threadId":7}}""");

        var (evt, body) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("stopped", evt);
        Assert.Equal(7, body.GetProperty("threadId").GetInt32());
    }

    [Fact]
    public async Task Reverse_request_without_handler_gets_unconditional_success()
    {
        _conn.Start();
        AdapterSend("""{"seq":42,"type":"request","command":"runInTerminal","arguments":{}}""");

        using var res = await Task.Run(AdapterReceive).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("response", res.RootElement.GetProperty("type").GetString());
        Assert.Equal(42, res.RootElement.GetProperty("request_seq").GetInt32());
        Assert.True(res.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Reverse_request_handler_receives_arguments_and_controls_success()
    {
        string? seenCommand = null;
        string? seenTargetId = null;
        _conn.ReverseRequestHandler = (command, args) =>
        {
            seenCommand = command;
            seenTargetId = args.GetProperty("configuration").GetProperty("__pendingTargetId").GetString();
            return false;   // 失敗応答を返させる
        };
        _conn.Start();

        AdapterSend("""{"seq":9,"type":"request","command":"startDebugging","arguments":{"request":"attach","configuration":{"__pendingTargetId":"t1"}}}""");

        using var res = await Task.Run(AdapterReceive).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("startDebugging", seenCommand);
        Assert.Equal("t1", seenTargetId);
        Assert.Equal(9, res.RootElement.GetProperty("request_seq").GetInt32());
        Assert.False(res.RootElement.GetProperty("success").GetBoolean());
    }
}
