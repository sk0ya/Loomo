using System.Collections.Concurrent;
using sk0ya.Loomo.Core.Pty;
using Terminal.Buffer;
using Terminal.Sessions;

namespace sk0ya.Loomo.Pty.Host;

/// <summary>
/// 生きているシェル1つ：ConPTY と、その出力を食べ続ける画面モデル。
///
/// <para>画面モデル（<see cref="HeadlessTerminal"/>）は WPF の値型を使い、スレッド安全でもないので、
/// 触るのは<b>このセッション専用のスレッド1本だけ</b>にする。出力の取り込み・繋ぎ替え・リサイズを全部
/// そこへ並べるので、「スナップショットを撮る」と「以後の出力を流し始める」の間に出力が割り込まない。</para>
///
/// <para>誰も繋がっていない間の問い合わせ（DSR・DA など）には画面モデルが答える。繋がっている間は
/// 本体の端末が答えるので黙る——二重に答えるとシェルに余計な入力が入る。</para>
/// </summary>
internal sealed class HostSession
{
    private readonly ConPtySession _pty;
    private readonly HeadlessTerminal _screen;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Action<HostSession> _ended;
    private ClientConnection? _client;
    private int _exitCode = -1;
    private int _finished;

    private HostSession(PtyProtocol.Request request, Action<HostSession> ended)
    {
        Id = request.SessionId;
        WorkspaceId = request.WorkspaceId;
        CreatedUtc = DateTime.UtcNow;
        WorkingDirectory = request.WorkingDirectory;
        _ended = ended;
        short columns = Math.Max(request.Columns, (short)20);
        short rows = Math.Max(request.Rows, (short)5);
        _screen = new HeadlessTerminal(columns, rows, request.ScrollbackLimit > 0 ? request.ScrollbackLimit : 10000);
        _screen.ResponseGenerated += OnScreenResponse;
        _pty = new ConPtySession(columns, rows, request.CommandLine!, request.WorkingDirectory, request.Environment);
        _pty.OutputReceived += (_, text) => Post(() => OnOutput(text));
        _pty.Exited += (_, code) => Post(() => Finish(code));
        new Thread(WorkLoop) { IsBackground = true, Name = $"PtyHostSession {Id:N}" }.Start();
        _pty.Start();
    }

    public Guid Id { get; }
    public Guid? WorkspaceId { get; }
    public DateTime CreatedUtc { get; }
    public volatile string? Title;
    public volatile string? WorkingDirectory;
    public volatile bool Attached;
    public bool HasFinished => Volatile.Read(ref _finished) != 0;

    public static HostSession Start(PtyProtocol.Request request, Action<HostSession> ended)
    {
        if (string.IsNullOrWhiteSpace(request.CommandLine))
            throw new ArgumentException("起動コマンド行がありません。");
        return new HostSession(request, ended);
    }

    public PtyProtocol.SessionInfo Describe() =>
        new(Id, WorkspaceId, Title, WorkingDirectory, CreatedUtc, Attached);

    /// <summary>
    /// 本体を繋ぐ。今の大きさに合わせてから画面を VT 列に組み直して最初の出力として渡し、
    /// 以後の出力をその後ろへ流す。前に繋がっていた接続には「取られた」と告げて閉じる。
    /// </summary>
    public void Attach(ClientConnection client, short columns, short rows) => Post(() =>
    {
        if (_client is { } previous && !ReferenceEquals(previous, client))
        {
            previous.Send(PtyProtocol.FrameType.TakenOver, []);
            previous.Close();
        }

        _client = client;
        Attached = true;
        ResizeOnThread(columns, rows);
        client.SendOutput(_screen.CreateVtSnapshot());
        if (HasFinished)
            client.SendExited(_exitCode);
    });

    public void Detach(ClientConnection client) => Post(() =>
    {
        if (ReferenceEquals(_client, client))
        {
            _client = null;
            Attached = false;
        }
    });

    public void Write(byte[] input)
    {
        try
        {
            _pty.Write(input);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            HostLog.Write($"入力に失敗 {Id:N}: {ex.Message}");
        }
    }

    public void Resize(short columns, short rows) => Post(() => ResizeOnThread(columns, rows));

    /// <summary>本体がタブを閉じた・作り直した。シェルごと殺す。</summary>
    public void Kill() => Post(() => Finish(-1));

    private void OnOutput(string text)
    {
        _screen.Process(text);
        _client?.SendOutput(text);
        Title = _screen.WindowTitle;
        if (_screen.CurrentDirectory is { } directory)
            WorkingDirectory = directory;
    }

    private void OnScreenResponse(object? sender, string text)
    {
        // 画面モデルのスレッドで呼ばれる（Process の中）。繋がっているなら本体が答える。
        if (_client is null)
            Write(System.Text.Encoding.UTF8.GetBytes(text));
    }

    private void ResizeOnThread(short columns, short rows)
    {
        if (columns <= 0 || rows <= 0 || (columns == _screen.Columns && rows == _screen.Rows))
            return;
        _screen.Resize(columns, rows);
        try
        {
            _pty.Resize(columns, rows);
        }
        catch (Exception ex)
        {
            HostLog.Write($"リサイズに失敗 {Id:N}: {ex.Message}");
        }
    }

    private void Finish(int exitCode)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return;
        _exitCode = exitCode;
        _client?.SendExited(exitCode);
        _client?.Close();
        _client = null;
        Attached = false;
        _ended(this);
        _work.CompleteAdding();
        // ConPTY の破棄は出力の読み取り側と待ち合うことがあるので、画面モデルのスレッドを塞がない。
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { _pty.Dispose(); }
            catch (Exception ex) { HostLog.Write($"破棄に失敗 {Id:N}: {ex.Message}"); }
        }, null);
    }

    private void Post(Action action)
    {
        try
        {
            _work.Add(action);
        }
        catch (InvalidOperationException)
        {
            // もう終わった。
        }
    }

    private void WorkLoop()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                HostLog.Write($"セッション処理で例外 {Id:N}: {ex}");
            }
        }
    }
}
