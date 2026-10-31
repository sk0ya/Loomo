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
    private readonly ManualResetEventSlim _readerDone = new(initialState: true);
    private Thread? _reader;

    internal RemotePtySession(NamedPipeClientStream pipe, bool created, int shellProcessId = 0)
    {
        _pipe = pipe;
        Created = created;
        ShellProcessId = shellProcessId;
    }

    /// <summary>新しく作ったシェルなら true、生きていたシェルに繋ぎ直したなら false。</summary>
    public bool Created { get; }

    /// <summary>ホストの上で動いているシェルの PID。古いホストは返さないので 0（分からない）。</summary>
    public int ShellProcessId { get; }

    public TerminalSessionCapabilities Capabilities { get; } = new(
        TerminalSessionKind.ConPty, SupportsResize: true, SupportsTerminalInput: true);

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<int>? Exited;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        // 読み取りはパイプの寿命ぶん塞がるので、スレッドプールではなく専用スレッド（§31.16）。
        _readerDone.Reset();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "RemotePtySessionReader" };
        _reader.Start();
    }

    public void Write(string input) => Write(Encoding.UTF8.GetBytes(input));

    public void Write(byte[] input)
    {
        if (input.Length > 0)
            Send(PtyProtocol.FrameType.Input, input);
    }

    public void Resize(short columns, short rows) =>
        Send(PtyProtocol.FrameType.Resize, PtyProtocol.EncodeResize(columns, rows));

    /// <summary>ConPTY はホストのプロセスにあるので、持ち主のウィンドウ（このタブを映している Loomo の窓）をホストへ運ぶ。
    /// これを知らない古いホストは読み捨てる（ダイアログが後ろに出るだけで、壊れはしない）。</summary>
    public void SetOwnerWindow(IntPtr ownerWindow) =>
        Send(PtyProtocol.FrameType.OwnerWindow, PtyProtocol.EncodeOwnerWindow(ownerWindow));

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
        // ホストが Kill を読み終える（＝接続を閉じる）まで待つ。待たないと、すぐ後に同じタブ ID で
        // 作り直しに来た Open が、まだ殺される前のシェルを掴んでしまう（再起動ボタン）。
        if (!ReferenceEquals(Thread.CurrentThread, _reader))
            _readerDone.Wait(TimeSpan.FromSeconds(2));
        try { _pipe.Dispose(); } catch { }
    }

    /// <summary>
    /// タブを閉じたが、<paramref name="keepFor"/> のあいだ殺さずにホストへ預ける（§24.41）。この接続は閉じるので、
    /// 以後の <see cref="Dispose"/> は何もしない（殺さない）。開き直すときは同じ ID で新しく繋ぐ。
    /// </summary>
    public void Park(TimeSpan keepFor)
    {
        var seconds = (int)Math.Clamp(keepFor.TotalSeconds, 1, 3600);
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, seconds);
        Send(PtyProtocol.FrameType.Park, payload);
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // ホストが Park を読み終える（＝接続を閉じる）まで待つ。待たずに捨てると、Park が届く前に切れて
        // ただの切り離し（期限なし）になりうる。
        if (!ReferenceEquals(Thread.CurrentThread, _reader))
            _readerDone.Wait(TimeSpan.FromSeconds(2));
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
        int exitCode = -1;
        try
        {
            while (PtyProtocol.ReadFrame(_pipe) is { } frame)
            {
                if (frame.Type == PtyProtocol.FrameType.Output)
                {
                    OutputReceived?.Invoke(this, PtyProtocol.DecodeText(frame.Payload));
                }
                else if (frame.Type == PtyProtocol.FrameType.Exited)
                {
                    exitCode = PtyProtocol.DecodeExitCode(frame.Payload);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
        }
        finally
        {
            // 終了を知らせる前に：受け手が UI スレッドへ同期で回すと、Dispose で待っている UI スレッドと詰む。
            _readerDone.Set();
        }

        RaiseExited(exitCode);
    }

    private void RaiseExited(int exitCode)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _exitRaised, 1) != 0)
            return;
        Exited?.Invoke(this, exitCode);
    }
}
