using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Lsp;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>
/// <see cref="IDebugService"/> の netcoredbg（DAP）実装。Phase 1 は「起動して実行し、標準出力/標準エラー/
/// 終了を観測する」までを担う。アダプタ駆動は <see cref="DapProtocolClient"/>（stdio 上の DAP）に委譲する。
///
/// 起動シーケンス（DAP 標準）：initialize →（response）→ launch を送信 →（adapter が initialized を送る）→
/// configurationDone → launch の response 完了で実行開始。launch の response は configurationDone の後に
/// 返るため、launch を await する前に initialized 受信で configurationDone を送る必要がある。
/// </summary>
public sealed class NetcoredbgDebugService : IDebugService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DapProtocolClient? _client;
    private int? _exitCode;
    private DebugSessionState _state = DebugSessionState.Idle;

    /// <summary>ソースパス（絶対）→ ブレークポイント行（1 始まり）。起動時の構成フェーズで再送する。</summary>
    private readonly Dictionary<string, IReadOnlyList<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>直近に停止したスレッド ID（continue/step の対象）。</summary>
    private int _stoppedThreadId;

    /// <summary>アタッチ（launch ではなく attach）でセッションを始めたか。停止時に <c>disconnect</c> へ
    /// <c>terminateDebuggee</c> を渡す/渡さないの判断に使う（アタッチ先プロセスは終了させない）。</summary>
    private bool _attached;

    /// <summary>
    /// launch/attach リクエストを stdin に書き終えたことを示すシグナル。initialized イベント由来の構成フェーズ
    /// （<see cref="ConfigureAsync"/>）はこれを待ってから configurationDone を送る。netcoredbg は
    /// launch/attach より先に configurationDone が来ると処理を取りこぼして 15 秒でタイムアウトするため、
    /// 「launch/attach を書く」→「configurationDone を送る」の順序をスレッド競合に依らず保証する。
    /// </summary>
    private TaskCompletionSource? _requestSent;

    public DebugSessionState State => _state;

    public bool IsAdapterAvailable => ExecutableResolver.IsOnPath(DebugAdapterCatalog.Netcoredbg.Executable);

    public event EventHandler<DebugSessionState>? StateChanged;
    public event EventHandler<DebugOutput>? Output;
    public event EventHandler<DebugStopped>? Stopped;
    public event EventHandler? Continued;
    public event EventHandler<DebugExited>? Exited;

    public Task StartAsync(DebugLaunchConfig config, CancellationToken ct)
    {
        var workDir = config.WorkingDirectory
            ?? Path.GetDirectoryName(config.Program)
            ?? Environment.CurrentDirectory;

        return BeginSessionAsync(
            requestCommand: "launch",
            buildArguments: () => new
            {
                name = "Loomo Debug",
                type = "coreclr",
                request = "launch",
                program = config.Program,
                args = config.Args ?? Array.Empty<string>(),
                cwd = workDir,
                stopAtEntry = config.StopAtEntry,
                justMyCode = false,
                console = "internalConsole",   // 出力を output イベントで受け取る
            },
            workDir: workDir,
            attaching: false,
            label: $"デバッグ起動: {config.Program}",
            failureLabel: "デバッグ起動に失敗しました",
            precheck: () => File.Exists(config.Program)
                ? null
                : $"実行対象が見つかりません: {config.Program}",
            ct: ct);
    }

    public Task AttachAsync(DebugAttachConfig config, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(config.Name) ? $"PID {config.ProcessId}" : $"{config.Name} (PID {config.ProcessId})";
        return BeginSessionAsync(
            requestCommand: "attach",
            buildArguments: () => new
            {
                name = "Loomo Attach",
                type = "coreclr",
                request = "attach",
                processId = config.ProcessId,
                justMyCode = false,
            },
            workDir: Environment.CurrentDirectory,
            attaching: true,
            label: $"アタッチ: {name}",
            failureLabel: "アタッチに失敗しました",
            precheck: () => config.ProcessId > 0 ? null : "アタッチ先のプロセス ID が不正です。",
            ct: ct);
    }

    /// <summary>launch / attach に共通するセッション開始シーケンス。<paramref name="requestCommand"/> 以外は
    /// 同一（initialize →（ここで await しない）launch/attach 送信 → 構成フェーズ → response 完了で Running）。
    /// <paramref name="precheck"/> は構成固有の事前検証（失敗理由を返す。null なら OK）。</summary>
    private async Task BeginSessionAsync(
        string requestCommand,
        Func<object> buildArguments,
        string workDir,
        bool attaching,
        string label,
        string failureLabel,
        Func<string?> precheck,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync();

            if (!IsAdapterAvailable)
            {
                Emit(DebugOutputCategory.Important,
                    $"デバッグアダプタ {DebugAdapterCatalog.Netcoredbg.Executable} が見つかりません。" +
                    $"インストールしてください（例: {DebugAdapterCatalog.Netcoredbg.InstallCommand}）。");
                SetState(DebugSessionState.Failed);
                return;
            }

            if (precheck() is { } reason)
            {
                Emit(DebugOutputCategory.Important, reason);
                SetState(DebugSessionState.Failed);
                return;
            }

            _exitCode = null;
            _attached = attaching;
            SetState(DebugSessionState.Launching);

            DapProtocolClient client;
            try
            {
                client = new DapProtocolClient(
                    DebugAdapterCatalog.Netcoredbg.Executable,
                    DebugAdapterCatalog.Netcoredbg.Args,
                    workDir);
            }
            catch (Exception ex)
            {
                Emit(DebugOutputCategory.Important, $"デバッグアダプタの起動に失敗しました: {ex.Message}");
                SetState(DebugSessionState.Failed);
                return;
            }

            _client = client;
            _requestSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.EventReceived += OnDapEvent;
            client.Exited += OnAdapterExited;

            try
            {
                await client.SendRequestAsync("initialize", new
                {
                    clientID = "loomo",
                    clientName = "Loomo",
                    adapterID = "netcoredbg",
                    locale = "ja",
                    linesStartAt1 = true,
                    columnsStartAt1 = true,
                    pathFormat = "path",
                    supportsRunInTerminalRequest = false,
                }, ct);

                // launch/attach は configurationDone の後に response が返るため、ここでは await しない。
                var requestTask = client.SendRequestAsync(requestCommand, buildArguments(), ct);

                // launch/attach を stdin に書き終えた（SendRequestAsync は WriteMessage 完了後に戻る）。
                // これで構成フェーズの configurationDone が launch/attach より先行しないことを保証する。
                _requestSent.TrySetResult();

                Emit(DebugOutputCategory.Important, label);
                await requestTask;
                SetState(DebugSessionState.Running);
            }
            catch (Exception ex)
            {
                Emit(DebugOutputCategory.Important, $"{failureLabel}: {ex.Message}");
                SetState(DebugSessionState.Failed);
                await StopCoreAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            // 手動停止では terminated イベントが Finish() に届かない（StopCoreAsync が購読解除済み）。
            // 状態を待機へ戻して StateChanged を発火させないと、IsBusy が true のまま固定され再実行できない。
            SetState(DebugSessionState.Idle);
        }
        finally { _gate.Release(); }
    }

    /// <summary>セッションを終了する（gate 取得済みの内部用）。</summary>
    private async Task StopCoreAsync()
    {
        var client = _client;
        // launch/attach 前に終了した場合に構成フェーズの待機を解放する（ぶら下がり防止）。
        _requestSent?.TrySetResult();
        if (client is null) return;
        _client = null;
        client.EventReceived -= OnDapEvent;
        client.Exited -= OnAdapterExited;

        if (client.IsRunning)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                // アタッチ時は対象プロセスを巻き込まない（デタッチのみ）。launch 時は終了させる。
                await client.SendRequestAsync("disconnect", new { terminateDebuggee = !_attached }, cts.Token);
            }
            catch { /* 既に終了/応答なし。Dispose で確実に殺す */ }
        }
        client.Dispose();
    }

    private void OnDapEvent(string evt, JsonElement body)
    {
        switch (evt)
        {
            case "initialized":
                // 構成フェーズ：記憶しているブレークポイントを全ソース分送ってから configurationDone。
                _ = ConfigureAsync();
                break;

            case "stopped":
                if (body.ValueKind == JsonValueKind.Object)
                {
                    var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? "stopped" : "stopped";
                    var threadId = body.TryGetProperty("threadId", out var ti) && ti.ValueKind == JsonValueKind.Number
                        ? ti.GetInt32() : 0;
                    _stoppedThreadId = threadId;
                    _ = HandleStoppedAsync(reason, threadId);
                }
                break;

            case "continued":
                SetState(DebugSessionState.Running);
                Continued?.Invoke(this, EventArgs.Empty);
                break;

            case "output":
                if (body.ValueKind == JsonValueKind.Object)
                {
                    var category = body.TryGetProperty("category", out var c) ? c.GetString() : "console";
                    var text = body.TryGetProperty("output", out var o) ? o.GetString() : null;
                    if (!string.IsNullOrEmpty(text) && category != "telemetry")
                        Emit(MapCategory(category), text!);
                }
                break;

            case "exited":
                if (body.ValueKind == JsonValueKind.Object &&
                    body.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Number)
                    _exitCode = ec.GetInt32();
                break;

            case "terminated":
                Finish("terminated イベント");
                break;
        }
    }

    private void OnAdapterExited() => Finish("アダプタ終了");

    /// <summary>セッション終了の確定処理（イベント or アダプタ終了から）。多重発火は状態で抑止。</summary>
    private void Finish(string reason)
    {
        if (_state is DebugSessionState.Terminated or DebugSessionState.Idle) return;
        var code = _exitCode;
        var client = _client;
        Emit(DebugOutputCategory.Important,
            $"デバッグ終了{(code is { } v ? $"（終了コード {v}）" : "")}");
        SetState(DebugSessionState.Terminated);
        Exited?.Invoke(this, new DebugExited(code, reason));

        // プログラムが自分で終了（terminated）してもアダプタは生き続ける。netcoredbg は対象の DLL/PDB を
        // 開いて握るため、残ると終了後に対象を再ビルドできない（「ファイル使用中」）。ここで確実に破棄する。
        // 対象は既に終了しているので disconnect は不要、破棄（プロセス kill）のみでよい。
        if (client is not null) _ = TearDownAdapterAsync(client);
    }

    /// <summary>セッション自然終了後にアダプタプロセスを破棄してファイルハンドルを解放する。
    /// 破棄中に別セッションへ置き換わっていたらそちらは壊さない（<see cref="ReferenceEquals"/> で判定）。</summary>
    private async Task TearDownAdapterAsync(DapProtocolClient client)
    {
        await _gate.WaitAsync();
        try
        {
            if (ReferenceEquals(_client, client))
            {
                _client = null;
                client.EventReceived -= OnDapEvent;
                client.Exited -= OnAdapterExited;
            }
            client.Dispose();  // Dispose は冪等。既に別セッションでも、この古いクライアントは確実に殺す。
        }
        finally { _gate.Release(); }
    }

    public async Task SetBreakpointsAsync(string sourcePath, IReadOnlyList<int> lines, CancellationToken ct)
    {
        var path = Path.GetFullPath(sourcePath);
        if (lines.Count == 0) _breakpoints.Remove(path);
        else _breakpoints[path] = lines;

        // 実行中（クライアント有り）なら即時反映。未起動なら構成フェーズで送られる。
        var client = _client;
        if (client is { IsRunning: true })
        {
            try { await SendBreakpointsAsync(client, path, lines, ct); }
            catch (Exception ex) { Emit(DebugOutputCategory.Console, $"ブレークポイント設定に失敗: {ex.Message}"); }
        }
    }

    public Task ContinueAsync() => StepRequestAsync("continue", resumes: true);
    public Task StepOverAsync() => StepRequestAsync("next", resumes: true);
    public Task StepInAsync() => StepRequestAsync("stepIn", resumes: true);
    public Task StepOutAsync() => StepRequestAsync("stepOut", resumes: true);

    /// <summary>continue/next/stepIn/stepOut を停止スレッドに対して送る。送信成功で Running へ。</summary>
    private async Task StepRequestAsync(string command, bool resumes)
    {
        var client = _client;
        if (client is not { IsRunning: true } || _state != DebugSessionState.Stopped) return;
        try
        {
            await client.SendRequestAsync(command, new { threadId = _stoppedThreadId });
            if (resumes)
            {
                SetState(DebugSessionState.Running);
                Continued?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"{command} に失敗: {ex.Message}"); }
    }

    public async Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync()
    {
        var client = _client;
        if (client is not { IsRunning: true } || _state != DebugSessionState.Stopped) return Array.Empty<DebugStackFrame>();
        try
        {
            var body = await client.SendRequestAsync("stackTrace",
                new { threadId = _stoppedThreadId, startFrame = 0, levels = 50 });
            var list = new List<DebugStackFrame>();
            if (body is { ValueKind: JsonValueKind.Object } b &&
                b.TryGetProperty("stackFrames", out var frames) && frames.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in frames.EnumerateArray())
                {
                    int id = f.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number ? idp.GetInt32() : 0;
                    var name = f.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                    int line = f.TryGetProperty("line", out var lp) && lp.ValueKind == JsonValueKind.Number ? lp.GetInt32() : 0;
                    string? path = f.TryGetProperty("source", out var sp) && sp.ValueKind == JsonValueKind.Object &&
                                   sp.TryGetProperty("path", out var pp) ? pp.GetString() : null;
                    list.Add(new DebugStackFrame(id, name, path, line));
                }
            }
            return list;
        }
        catch { return Array.Empty<DebugStackFrame>(); }
    }

    public async Task<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId)
    {
        var client = _client;
        if (client is not { IsRunning: true }) return Array.Empty<DebugScope>();
        try
        {
            var body = await client.SendRequestAsync("scopes", new { frameId });
            var list = new List<DebugScope>();
            if (body is { ValueKind: JsonValueKind.Object } b &&
                b.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in scopes.EnumerateArray())
                {
                    var name = s.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                    int vr = s.TryGetProperty("variablesReference", out var vp) && vp.ValueKind == JsonValueKind.Number ? vp.GetInt32() : 0;
                    bool exp = s.TryGetProperty("expensive", out var ep) && ep.ValueKind == JsonValueKind.True;
                    list.Add(new DebugScope(name, vr, exp));
                }
            }
            return list;
        }
        catch { return Array.Empty<DebugScope>(); }
    }

    public async Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference)
    {
        var client = _client;
        if (client is not { IsRunning: true } || variablesReference <= 0) return Array.Empty<DebugVariable>();
        try
        {
            var body = await client.SendRequestAsync("variables", new { variablesReference });
            var list = new List<DebugVariable>();
            if (body is { ValueKind: JsonValueKind.Object } b &&
                b.TryGetProperty("variables", out var vars) && vars.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vars.EnumerateArray())
                {
                    var name = v.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                    var value = v.TryGetProperty("value", out var vp) ? vp.GetString() ?? "" : "";
                    var type = v.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    int vr = v.TryGetProperty("variablesReference", out var rp) && rp.ValueKind == JsonValueKind.Number ? rp.GetInt32() : 0;
                    list.Add(new DebugVariable(name, value, type, vr));
                }
            }
            return list;
        }
        catch { return Array.Empty<DebugVariable>(); }
    }

    public async Task<string> EvaluateAsync(string expression, int? frameId)
    {
        var client = _client;
        if (client is not { IsRunning: true }) return "(セッションがありません)";
        try
        {
            var body = await client.SendRequestAsync("evaluate",
                new { expression, frameId, context = "watch" });
            if (body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("result", out var r))
                return r.GetString() ?? "";
            return "";
        }
        catch (Exception ex) { return $"(評価エラー: {ex.Message})"; }
    }

    /// <summary>構成フェーズ：記憶している全ブレークポイントを送り、最後に configurationDone を送る。</summary>
    private async Task ConfigureAsync()
    {
        var client = _client;
        if (client is null) return;

        // launch/attach が stdin に書かれるまで待つ。先に configurationDone を送ると netcoredbg の
        // launch/attach がタイムアウトする（順序保証。await initialize の戻りと initialized イベントは競合し得る）。
        var requestSent = _requestSent;
        if (requestSent is not null)
        {
            try { await requestSent.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* タイムアウト/未設定でも configurationDone は送る（従来動作にフォールバック） */ }
        }

        foreach (var (path, lines) in _breakpoints)
        {
            try { await SendBreakpointsAsync(client, path, lines, CancellationToken.None); }
            catch { /* 1 ソースの失敗は他に波及させない */ }
        }
        try { await client.SendRequestAsync("configurationDone", null); }
        catch { /* netcoredbg は configurationDone 不要でも動くことがある */ }
    }

    private static Task SendBreakpointsAsync(DapProtocolClient client, string path, IReadOnlyList<int> lines, CancellationToken ct)
        => client.SendRequestAsync("setBreakpoints", new
        {
            source = new { path, name = Path.GetFileName(path) },
            breakpoints = lines.Select(l => new { line = l }).ToArray(),
        }, ct);

    /// <summary>stopped の際、停止位置（ソース・行）を stackTrace から引いて通知する。</summary>
    private async Task HandleStoppedAsync(string reason, int threadId)
    {
        SetState(DebugSessionState.Stopped);
        string? sourcePath = null;
        int line = 0;
        try
        {
            var client = _client;
            if (client is { IsRunning: true })
            {
                var body = await client.SendRequestAsync("stackTrace",
                    new { threadId, startFrame = 0, levels = 1 });
                if (body is { ValueKind: JsonValueKind.Object } b &&
                    b.TryGetProperty("stackFrames", out var frames) &&
                    frames.ValueKind == JsonValueKind.Array && frames.GetArrayLength() > 0)
                {
                    var top = frames[0];
                    if (top.TryGetProperty("line", out var ln) && ln.ValueKind == JsonValueKind.Number)
                        line = ln.GetInt32();
                    if (top.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object &&
                        src.TryGetProperty("path", out var sp))
                        sourcePath = sp.GetString();
                }
            }
        }
        catch { /* スタック取得失敗時は位置不明のまま通知 */ }

        Stopped?.Invoke(this, new DebugStopped(sourcePath, line, reason, threadId));
    }

    private static DebugOutputCategory MapCategory(string? category) => category switch
    {
        "stdout" => DebugOutputCategory.Stdout,
        "stderr" => DebugOutputCategory.Stderr,
        _ => DebugOutputCategory.Console,
    };

    private void Emit(DebugOutputCategory category, string text)
        => Output?.Invoke(this, new DebugOutput(category, text));

    private void SetState(DebugSessionState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }
}
