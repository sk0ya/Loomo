using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace sk0ya.Loomo.App.Services;

/// <summary>起動中の部屋どうしで「開いてほしいもの」を受け渡す名前付きパイプ（既定のアプリとしての振る舞い）。
///
/// 部屋＝プロセスなので、エクスプローラーがファイルを開くたびに起こす新しいプロセスは、まず既存の部屋へ
/// 問い合わせ（<c>probe</c>）、受け手が決まれば渡して（<c>open</c>）すぐ終わる。各部屋はプロセスごとに
/// 自分の名前でパイプを立てるので、単一インスタンス用のミューテックスのような「部屋は1つ」の前提は持ち込まない。
/// 通信は1接続1往復・1行の JSON。<c>CurrentUserOnly</c> で同じ利用者のプロセスにしか開かない。</summary>
internal static class InstanceRelay
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>同じログオンセッション内で一意な、そのプロセスのパイプ名。</summary>
    public static string PipeName(int processId)
        => $"sk0ya.Loomo.Relay.{Process.GetCurrentProcess().SessionId}.{processId}";

    internal sealed record Message(
        string Op,
        string? Folder = null,
        string[]? Files = null,
        string[]? Urls = null);

    internal sealed record Reply(
        bool Ok,
        string[]? Folders = null,
        DateTime LastActiveUtc = default);

    internal static async Task<Reply?> SendAsync(string pipeName, Message message, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            await WriteLineAsync(pipe, JsonSerializer.Serialize(message, Json), cts.Token).ConfigureAwait(false);
            var line = await ReadLineAsync(pipe, cts.Token).ConfigureAwait(false);
            return line is null ? null : JsonSerializer.Deserialize<Reply>(line, Json);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException
                                       or UnauthorizedAccessException or JsonException)
        {
            // 落ちかけ・起動途中・別利用者の部屋は「いない」扱い。
            return null;
        }
    }

    internal static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0)
                break;
            var newline = Array.IndexOf(one, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                buffer.Write(one, 0, newline);
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
            buffer.Write(one, 0, read);
            if (buffer.Length > 1024 * 1024)
                return null;   // 1行がこれより長い要求は来ない（パス数個ぶん）。壊れた相手とみなす
        }
        return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>自分の部屋が立てる受け口。起動の早い段階（ウィンドウより前）から受け付け、
/// ウィンドウが開けるようになるまで届いた要求は溜めておく。</summary>
internal sealed class InstanceRelayServer : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<ExternalOpenRequest> _pending = [];
    private Action<ExternalOpenRequest>? _handler;
    private volatile string[] _folders = [];
    private long _lastActiveTicks = DateTime.UtcNow.Ticks;

    public InstanceRelayServer(string pipeName)
    {
        _pipeName = pipeName;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>問い合わせに答えるフォルダー集合。UI スレッドで書き、パイプ側は読むだけ（配列ごと差し替える）。</summary>
    public void SetFolders(IEnumerable<string> folders) => _folders = folders.ToArray();

    /// <summary>この部屋が前面へ来た。「直近の部屋」の判定に使う。</summary>
    public void MarkActive() => Interlocked.Exchange(ref _lastActiveTicks, DateTime.UtcNow.Ticks);

    /// <summary>要求の受け手を据える。それまでに溜まっていた要求はここで渡す。
    /// 受け手はパイプのスレッドから呼ばれるので、UI へ戻すのは受け手の仕事。</summary>
    public void SetHandler(Action<ExternalOpenRequest> handler)
    {
        ExternalOpenRequest[] pending;
        lock (_gate)
        {
            _handler = handler;
            pending = _pending.ToArray();
            _pending.Clear();
        }
        foreach (var request in pending)
            handler(request);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => ServeAsync(connected));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[中継] 受け口のエラー: {ex.Message}");
                try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var line = await InstanceRelay.ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (line is null)
                return;
            var message = JsonSerializer.Deserialize<InstanceRelay.Message>(line, InstanceRelay.Json);
            var reply = message?.Op switch
            {
                "probe" => new InstanceRelay.Reply(true, _folders,
                    new DateTime(Interlocked.Read(ref _lastActiveTicks), DateTimeKind.Utc)),
                "open" => Accept(message),
                _ => new InstanceRelay.Reply(false),
            };
            await InstanceRelay.WriteLineAsync(pipe,
                JsonSerializer.Serialize(reply, InstanceRelay.Json), timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
        {
            // 相手が途中で消えた。受け口は次の接続を待ち続ける。
        }
    }

    private InstanceRelay.Reply Accept(InstanceRelay.Message message)
    {
        var request = new ExternalOpenRequest(message.Folder, message.Files ?? [], message.Urls ?? []);
        if (request.IsEmpty)
            return new InstanceRelay.Reply(true);
        Action<ExternalOpenRequest>? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler is null)
                _pending.Add(request);
        }
        handler?.Invoke(request);
        return new InstanceRelay.Reply(true);
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>新しく起こされたプロセス側：起動中の部屋を探し、渡せるものを渡す。</summary>
internal static class InstanceRelayClient
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(3);

    /// <summary>同じ利用者・同じセッションで動いている他の部屋に問い合わせる。答えない部屋は数に入れない。</summary>
    public static async Task<IReadOnlyList<RelayInstance>> ProbeAsync()
    {
        var self = Process.GetCurrentProcess();
        Process[] candidates;
        try { candidates = Process.GetProcessesByName(self.ProcessName); }
        catch (Exception) { return []; }

        var probes = candidates
            .Where(p => p.Id != self.Id && SafeSessionId(p) == self.SessionId)
            .Select(async p =>
            {
                var reply = await InstanceRelay.SendAsync(
                    InstanceRelay.PipeName(p.Id), new InstanceRelay.Message("probe"), ProbeTimeout).ConfigureAwait(false);
                return reply is { Ok: true }
                    ? new RelayInstance(p.Id, reply.Folders ?? [], reply.LastActiveUtc)
                    : null;
            })
            .ToList();
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        foreach (var p in candidates)
            p.Dispose();
        return results.OfType<RelayInstance>().ToList();
    }

    /// <summary>渡す。受け手が前面へ出られるよう、先に前面化の権利を譲っておく（エクスプローラーから
    /// 起こされたこのプロセスは前面化を許されているが、受け手の部屋は許されていない）。</summary>
    public static async Task<bool> OpenAsync(int processId, ExternalOpenRequest request)
    {
        AllowSetForegroundWindow(processId);
        var reply = await InstanceRelay.SendAsync(
            InstanceRelay.PipeName(processId),
            new InstanceRelay.Message("open", request.WorkspaceFolder, request.Files.ToArray(), request.Urls.ToArray()),
            OpenTimeout).ConfigureAwait(false);
        return reply is { Ok: true };
    }

    private static int SafeSessionId(Process process)
    {
        try { return process.SessionId; }
        catch (Exception) { return -1; }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
