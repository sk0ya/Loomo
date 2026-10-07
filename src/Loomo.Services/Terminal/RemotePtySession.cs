using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Pty;
using Terminal.Sessions;

namespace sk0ya.Loomo.Services.Terminal;

/// <summary>
/// 常駐ホストが持つシェルへの接続（設計 §34）。端末ビューから見れば ConPtySession と同じ
/// <see cref="ITerminalSession"/> で、最初の出力がホストの組み立てた画面のスナップショットになる。
///
/// <para><b><see cref="Dispose"/> は「殺す」</b>（タブを閉じた・作り直した）。Loomo の終了ではビューは
/// セッションを Dispose しないので、プロセスが消えてパイプが切れる＝切り離しになり、シェルは残る（§34.6）。</para>
/// </summary>
public sealed class RemotePtySession : ITerminalSession
{
    private readonly NamedPipeClientStream _pipe;
    private readonly object _writeGate = new();
    private int _started;
    private int _disposed;
    private int _exitRaised;

    internal RemotePtySession(NamedPipeClientStream pipe, bool created)
    {
        _pipe = pipe;
        Created = created;
    }

    /// <summary>新しく作ったシェルなら true、生きていたシェルに繋ぎ直したなら false。</summary>
    public bool Created { get; }

    public TerminalSessionCapabilities Capabilities { get; } = new(
        TerminalSessionKind.ConPty, SupportsResize: true, SupportsTerminalInput: true);

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<int>? Exited;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        // 読み取りはパイプの寿命ぶん塞がるので、スレッドプールではなく専用スレッド（§31.16）。
        new Thread(ReadLoop) { IsBackground = true, Name = "RemotePtySessionReader" }.Start();
    }

    public void Write(string input) => Write(Encoding.UTF8.GetBytes(input));

    public void Write(byte[] input)
    {
        if (input.Length > 0)
            Send(PtyProtocol.FrameType.Input, input);
    }

    public void Resize(short columns, short rows) =>
        Send(PtyProtocol.FrameType.Resize, PtyProtocol.EncodeResize(columns, rows));

    /// <summary>ホストの向こうのシェルは止まって見えても読み取りは詰まらない（ホストが読み続ける）。</summary>
    public bool IsOutputStalled(TimeSpan initialOutputTimeout, TimeSpan idleOutputTimeout) => false;

    public bool TryForceUnlock(uint exitCode = 1)
    {
        Send(PtyProtocol.FrameType.Kill, []);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Send(PtyProtocol.FrameType.Kill, [], evenIfDisposed: true);
        try { _pipe.Dispose(); } catch { }
    }

    /// <summary>殺さずに接続だけ捨てる——Loomo のプロセスが消えたときと同じ（テスト用）。</summary>
    internal void Abandon()
    {
        Interlocked.Exchange(ref _disposed, 1);
        try { _pipe.Dispose(); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void Send(PtyProtocol.FrameType type, byte[] payload, bool evenIfDisposed = false)
    {
        if (!evenIfDisposed && Volatile.Read(ref _disposed) != 0)
            return;
        lock (_writeGate)
        {
            try
            {
                PtyProtocol.WriteFrame(_pipe, type, payload);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // ホストが落ちた。読み取り側が終了として伝える。
            }
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (PtyProtocol.ReadFrame(_pipe) is { } frame)
            {
                switch (frame.Type)
                {
                    case PtyProtocol.FrameType.Output:
                        OutputReceived?.Invoke(this, PtyProtocol.DecodeText(frame.Payload));
                        break;
                    case PtyProtocol.FrameType.Exited:
                        RaiseExited(PtyProtocol.DecodeExitCode(frame.Payload));
                        return;
                    case PtyProtocol.FrameType.TakenOver:
                        // 別の Loomo が同じシェルを取った。こちらから見ればもう流れない。
                        RaiseExited(-1);
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
        }

        RaiseExited(-1);
    }

    private void RaiseExited(int exitCode)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _exitRaised, 1) != 0)
            return;
        Exited?.Invoke(this, exitCode);
    }
}
