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
    private ClientConnection? _owner;
    private int _exitCode = -1;
    private int _finished;
    private volatile bool _killRequested;

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
    public bool Attached => Volatile.Read(ref _owner) is not null;
    /// <summary>終わった、または殺すよう頼まれた（殺しは画面モデルのスレッドで後から起きるが、同じ ID で
    /// 作り直しに来た Open にはもう渡さない）。</summary>
    public bool HasFinished => _killRequested || Volatile.Read(ref _finished) != 0;

    public static HostSession Start(PtyProtocol.Request request, Action<HostSession> ended)
    {
        if (string.IsNullOrWhiteSpace(request.CommandLine))
            throw new ArgumentException("起動コマンド行がありません。");
        return new HostSession(request, ended);
    }

    public PtyProtocol.SessionInfo Describe() =>
        new(Id, WorkspaceId, Title, WorkingDirectory, CreatedUtc, Attached);

    /// <summary>
    /// このシェルを <paramref name="client"/> の持ち物にする。先勝ち：もう誰かが繋がっていれば false で、
    /// 後から来た方には渡さない（奪うと先の Loomo の画面が黙って止まる）。持ち主の接続が切れれば
    /// <see cref="Detach"/> で空くので、落ちた Loomo の再起動は普通に繋がる。
    /// </summary>
    public bool TryClaim(ClientConnection client) =>
        Interlocked.CompareExchange(ref _owner, client, null) is null;

    /// <summary>
    /// 持ち主（<see cref="TryClaim"/> 済み）を繋ぐ。今の大きさに合わせてから画面を VT 列に組み直して
    /// 最初の出力として渡し、以後の出力をその後ろへ流す。
    /// </summary>
    public void Attach(ClientConnection client, short columns, short rows)
    {
        // Open が見てから TryClaim までの間にシェルが終わっていたら、Attach はもう並ばない。
        // 黙って捨てると本体は応答だけ受けて何も流れてこない空のタブになるので、終わったと伝える。
        if (!Post(() => AttachOnThread(client, columns, rows)))
        {
            client.SendExited(_exitCode);
            client.Close();
        }
    }

    private void AttachOnThread(ClientConnection client, short columns, short rows)
    {
        if (!ReferenceEquals(Volatile.Read(ref _owner), client))
            return;
        _client = client;
        ResizeOnThread(columns, rows);
        client.SendOutput(_screen.CreateVtSnapshot());
        if (Volatile.Read(ref _finished) != 0)
            client.SendExited(_exitCode);
    }

    public void Detach(ClientConnection client)
    {
        // 持ち主の席はすぐ空ける（再起動した Loomo が待たされない）。出力の流し先は画面モデルのスレッドで外す。
        Interlocked.CompareExchange(ref _owner, null, client);
        Post(() =>
        {
            if (ReferenceEquals(_client, client))
                _client = null;
        });
    }

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
    public void Kill()
    {
        _killRequested = true;
        Post(() => Finish(-1));
    }

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
        Volatile.Write(ref _owner, null);
        _ended(this);
        _work.CompleteAdding();
        // ConPTY の破棄は出力の読み取り側と待ち合うことがあるので、画面モデルのスレッドを塞がない。
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { _pty.Dispose(); }
            catch (Exception ex) { HostLog.Write($"破棄に失敗 {Id:N}: {ex.Message}"); }
        }, null);
    }

    private bool Post(Action action)
    {
        try
        {
            _work.Add(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            // もう終わった。
            return false;
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
