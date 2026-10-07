using System.IO;
using System.IO.Pipes;
using sk0ya.Loomo.Core.Pty;

namespace sk0ya.Loomo.Pty.Host;

/// <summary>
/// パイプの受付とセッション表。接続ごとに1本のスレッドで、最初の要求を読んで振り分ける。
/// 本体との接続が切れてもセッションは殺さない——それが常駐の意味（§34.6）。
/// </summary>
/// <param name="pipeName">待ち受けるパイプ名。</param>
/// <param name="idleExit">セッションが無く、誰も繋がっていない状態がこれだけ続いたら終わる。null なら終わらない（テスト用）。</param>
internal sealed class PtyHostServer(string pipeName, TimeSpan? idleExit)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HostSession> _sessions = new();
    private int _connections;
    private DateTime _idleSinceUtc = DateTime.UtcNow;

    public void Run()
    {
        if (idleExit is { } limit)
            new Thread(() => IdleWatch(limit)) { IsBackground = true, Name = "PtyHostIdleWatch" }.Start();
        while (true)
        {
            // Asynchronous（overlapped）は必須。同期ハンドルだと Windows は同じハンドルの I/O を直列にするので、
            // 読み取りスレッドが ReadFile で待っている間、書き出しスレッドの WriteFile も止まる（両端で詰む）。
            var pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                pipe.WaitForConnection();
            }
            catch (Exception ex)
            {
                HostLog.Write($"接続の受付に失敗: {ex.Message}");
                pipe.Dispose();
                Thread.Sleep(200);
                continue;
            }

            lock (_gate)
                _connections++;
            new Thread(() => Serve(pipe)) { IsBackground = true, Name = "PtyHostConnection" }.Start();
        }
    }

    private void Serve(NamedPipeServerStream pipe)
    {
        ClientConnection? client = null;
        HostSession? session = null;
        try
        {
            if (PtyProtocol.ReadFrame(pipe) is not { Type: PtyProtocol.FrameType.Request } frame)
                return;
            var request = PtyProtocol.ReadJson<PtyProtocol.Request>(frame.Payload);
            client = new ClientConnection(pipe);
            switch (request.Op)
            {
                case PtyProtocol.Operation.List:
                    client.Send(PtyProtocol.FrameType.Response, Json(new PtyProtocol.Response(Sessions: List())));
                    return;
                case PtyProtocol.Operation.Kill:
                    Find(request.SessionId)?.Kill();
                    client.Send(PtyProtocol.FrameType.Response, Json(new PtyProtocol.Response()));
                    return;
                case PtyProtocol.Operation.KillWorkspace:
                    foreach (var doomed in SessionsOf(request.WorkspaceId))
                        doomed.Kill();
                    client.Send(PtyProtocol.FrameType.Response, Json(new PtyProtocol.Response()));
                    return;
                case PtyProtocol.Operation.Open:
                    bool created;
                    try
                    {
                        (session, created) = OpenSession(request);
                    }
                    catch (Exception ex)
                    {
                        HostLog.Write($"セッションを作れない {request.SessionId:N}: {ex.Message}");
                        client.Send(PtyProtocol.FrameType.Error, System.Text.Encoding.UTF8.GetBytes(ex.Message));
                        return;
                    }

                    client.Send(PtyProtocol.FrameType.Response, Json(new PtyProtocol.Response(Created: created)));
                    session.Attach(client, request.Columns, request.Rows);
                    ReadInput(pipe, session);
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            // 本体が閉じた・落ちた。セッションはそのまま残る。
        }
        catch (Exception ex)
        {
            HostLog.Write($"接続の処理で例外: {ex}");
        }
        finally
        {
            if (session is not null && client is not null)
                session.Detach(client);
            if (client is not null)
                client.Close();
            else
                pipe.Dispose();
            lock (_gate)
            {
                _connections--;
                _idleSinceUtc = DateTime.UtcNow;
            }
        }
    }

    private static void ReadInput(Stream pipe, HostSession session)
    {
        while (PtyProtocol.ReadFrame(pipe) is { } frame)
        {
            switch (frame.Type)
            {
                case PtyProtocol.FrameType.Input:
                    session.Write(frame.Payload);
                    break;
                case PtyProtocol.FrameType.Resize:
                    var (columns, rows) = PtyProtocol.DecodeResize(frame.Payload);
                    session.Resize(columns, rows);
                    break;
                case PtyProtocol.FrameType.Kill:
                    session.Kill();
                    return;
            }
        }
    }

    private (HostSession Session, bool Created) OpenSession(PtyProtocol.Request request)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(request.SessionId, out var existing) && !existing.HasFinished)
                return (existing, false);
        }

        // ConPTY とシェルの起動は数百 ms かかるので、表の鍵の外でやる。同じ ID の二重 Open は
        // 本体の作りでは起きない（1タブ1接続）が、起きたら後から来た方を捨てて先の方へ繋ぐ。
        var session = HostSession.Start(request, OnSessionEnded);
        lock (_gate)
        {
            if (_sessions.TryGetValue(request.SessionId, out var raced) && !raced.HasFinished)
            {
                session.Kill();
                return (raced, false);
            }

            _sessions[request.SessionId] = session;
        }

        HostLog.Write($"セッション開始 {request.SessionId:N} ws={request.WorkspaceId:N} cwd={request.WorkingDirectory}");
        return (session, true);
    }

    private void OnSessionEnded(HostSession session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(session.Id, out var current) && ReferenceEquals(current, session))
                _sessions.Remove(session.Id);
            _idleSinceUtc = DateTime.UtcNow;
        }

        HostLog.Write($"セッション終了 {session.Id:N}");
    }

    private HostSession? Find(Guid id)
    {
        lock (_gate)
            return _sessions.GetValueOrDefault(id);
    }

    private List<HostSession> SessionsOf(Guid? workspaceId)
    {
        lock (_gate)
            return _sessions.Values.Where(s => workspaceId is { } id && s.WorkspaceId == id).ToList();
    }

    private List<PtyProtocol.SessionInfo> List()
    {
        lock (_gate)
            return _sessions.Values.Select(s => s.Describe()).ToList();
    }

    private void IdleWatch(TimeSpan limit)
    {
        while (true)
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            lock (_gate)
            {
                if (_sessions.Count > 0 || _connections > 0 || DateTime.UtcNow - _idleSinceUtc < limit)
                    continue;
            }

            HostLog.Write("セッションも接続も無いので終了");
            Environment.Exit(0);
        }
    }

    private static byte[] Json<T>(T value) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, PtyProtocol.Json);
}
