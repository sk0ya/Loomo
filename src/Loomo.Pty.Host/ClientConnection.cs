using System.Collections.Concurrent;
using System.IO;
using sk0ya.Loomo.Core.Pty;

namespace sk0ya.Loomo.Pty.Host;

/// <summary>
/// 本体からの接続1本。書き出しは専用スレッド＋キューで、遅い（あるいは固まった）本体が
/// ConPTY の読み取りや画面モデルを止めないようにする（§31.16 と同じ理由）。
/// </summary>
internal sealed class ClientConnection
{
    private readonly Stream _pipe;
    private readonly BlockingCollection<(PtyProtocol.FrameType Type, byte[] Payload)> _queue = new();

    public ClientConnection(Stream pipe)
    {
        _pipe = pipe;
        new Thread(WriteLoop) { IsBackground = true, Name = "PtyHostClientWriter" }.Start();
    }

    public void Send(PtyProtocol.FrameType type, byte[] payload)
    {
        try
        {
            _queue.Add((type, payload));
        }
        catch (InvalidOperationException)
        {
            // もう閉じた。
        }
    }

    public void SendOutput(string text) => Send(PtyProtocol.FrameType.Output, PtyProtocol.EncodeText(text));

    public void SendExited(int exitCode) => Send(PtyProtocol.FrameType.Exited, PtyProtocol.EncodeExitCode(exitCode));

    /// <summary>キューに積んだ分を書き切ってから閉じる。</summary>
    public void Close() => _queue.CompleteAdding();

    private void WriteLoop()
    {
        try
        {
            foreach (var (type, payload) in _queue.GetConsumingEnumerable())
                PtyProtocol.WriteFrame(_pipe, type, payload);
        }
        catch
        {
            // 本体が閉じた。読み取り側が切断として片付ける。
        }
        finally
        {
            _queue.CompleteAdding();
            try { _pipe.Dispose(); } catch { }
        }
    }
}
