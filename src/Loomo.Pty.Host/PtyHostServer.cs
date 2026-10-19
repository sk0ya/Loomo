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
/// <param name="lastClientLeft">最後の Loomo が終わり、全セッションを殺したあとに呼ぶ（本番はプロセスを終える）。</param>
internal sealed class PtyHostServer(string pipeName, TimeSpan? idleExit, Action? lastClientLeft = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HostSession> _sessions = new();
    /// <summary>要求をくれた Loomo のプロセス。正常終了の <c>Leave</c> で外し、落ちたものは生死で数えない。</summary>
    private readonly Dictionary<int, System.Diagnostics.Process> _clients = new();
    private int _connections;
    private DateTime _idleSinceUtc = DateTime.UtcNow;

    public void Run()
    {
        if (idleExit is { } limit)
            new Thread(() => IdleWatch(limit)) { IsBackground = true, Name = "PtyHostIdleWatch" }.Start();
        new Thread(ParkWatch) { IsBackground = true, Name = "PtyHostParkWatch" }.Start();
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
            if (request.Op != PtyProtocol.Operation.Leave)
                RememberClient(request.ClientProcessId);
            switch (request.Op)
            {
                case PtyProtocol.Operation.Leave:
                    bool last = ForgetClient(request.ClientProcessId);
                    if (last)
                        KillAll();
                    client.Send(PtyProtocol.FrameType.Response, Json(new PtyProtocol.Response()));
                    if (last && lastClientLeft is { } leave)
                    {
                        HostLog.Write("最後の Loomo が終わったので終了");
                        // 返事を書き切ってから終わる（Loomo は返事を待ってから自分の終了を進める）。
                        client.Close();
                        Thread.Sleep(200);
                        leave();
                    }
                    return;
                case PtyProtocol.Operation.List:
                    client.Send(PtyProtocol.FrameType.Response, Json(new PtyProtocol.Response(Sessions: List())));
                    return;
                case PtyProtocol.Operation.Kill:
                    // 外からの Kill は、誰も繋いでいないシェルにだけ効く。繋いでいる Loomo は自分の接続で殺す
                    // （タブを閉じた）ので、ここで繋がっているものを殺すと別の Loomo のシェルを奪うことになる。
                    if (Find(request.SessionId) is { Attached: false } target)
                        target.Kill();
                    client.Send(PtyProtocol.FrameType.Response, Json(new PtyProtocol.Response()));
                    return;
                case PtyProtocol.Operation.KillWorkspace:
                    foreach (var doomed in SessionsOf(request.WorkspaceId).Where(s => !s.Attached))
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

                    if (!session.TryClaim(client))
                    {
                        // 先勝ち。後から来た方には渡さない（finally で Detach されても持ち主ではないので何も起きない）。
                        client.Send(PtyProtocol.FrameType.Response, Json(new PtyProtocol.Response(InUse: true)));
                        session = null;
                        return;
                    }

                    client.Send(PtyProtocol.FrameType.Response, Json(new PtyProtocol.Response(Created: created, ShellProcessId: session.ShellProcessId)));
                    session.ParkedUntilUtc = null;   // 閉じたタブを開き直した（§24.41）
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
                case PtyProtocol.FrameType.Park:
                    var seconds = frame.Payload.Length >= 4
                        ? System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(frame.Payload) : 0;
                    session.ParkedUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600));
                    HostLog.Write($"閉じたタブを預かる {session.Id:N} {seconds}秒");
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

    private void RememberClient(int processId)
    {
        if (processId <= 0)
            return;
        lock (_gate)
        {
            if (_clients.ContainsKey(processId))
                return;
        }

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return; // もういない。
        }

        lock (_gate)
        {
            if (!_clients.TryAdd(processId, process))
                process.Dispose();
        }
    }

    /// <summary><paramref name="processId"/> を外し、ほかに生きている Loomo が残っていなければ true。</summary>
    private bool ForgetClient(int processId)
    {
        // 抜けるのは最後の「話したことのある」Loomo でも、まだホストと話していない Loomo（端末を出していない・
        // 保存タブの無いワークスペース）が生きているかもしれない。その Loomo も後で常駐セッションへ戻るので、
        // 同じ実行ファイル名のプロセスがほかに生きていれば最後とは見なさない（殺さない側に倒す）。
        var processName = ProcessNameOf(processId);
        lock (_gate)
        {
            if (_clients.Remove(processId, out var leaving))
                leaving.Dispose();
            foreach (var (id, process) in _clients.ToList())
            {
                bool exited;
                try { exited = process.HasExited; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { exited = true; }
                if (!exited)
                    continue;
                _clients.Remove(id);
                process.Dispose();
            }
            if (_clients.Count > 0)
                return false;
        }
        return !OtherProcessAlive(processName, processId);
    }

    private static string? ProcessNameOf(int processId)
    {
        if (processId <= 0)
            return null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    private static bool OtherProcessAlive(string? processName, int exceptId)
    {
        if (string.IsNullOrEmpty(processName))
            return false;
        var others = System.Diagnostics.Process.GetProcessesByName(processName);
        try
        {
            return others.Any(p => p.Id != exceptId);
        }
        finally
        {
            foreach (var p in others)
                p.Dispose();
        }
    }

    /// <summary>
    /// 全セッションを殺し、表から消えるまで（最大2秒）待つ。シェルは ConPTY の kill-on-close ジョブに入っているので、
    /// 待ち切れずにホストが終わっても道連れになる。
    /// </summary>
    private void KillAll()
    {
        List<HostSession> doomed;
        lock (_gate)
            doomed = _sessions.Values.ToList();
        foreach (var session in doomed)
            session.Kill();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_sessions.Count == 0)
                    return;
            }
            Thread.Sleep(20);
        }
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

    /// <summary>預かった期限を過ぎ、開き直されなかったシェルを殺す（§24.41）。</summary>
    private void ParkWatch()
    {
        while (true)
        {
            Thread.Sleep(TimeSpan.FromSeconds(2));
            List<HostSession> expired;
            var now = DateTime.UtcNow;
            lock (_gate)
                expired = _sessions.Values.Where(s => !s.Attached && s.ParkedUntilUtc is { } until && until <= now).ToList();
            foreach (var session in expired)
            {
                HostLog.Write($"預かった期限が切れたので終了 {session.Id:N}");
                session.Kill();
            }
        }
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
