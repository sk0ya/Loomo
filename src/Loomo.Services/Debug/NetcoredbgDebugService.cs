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

    /// <summary>ソースパス（絶対）→ ブレークポイント（1 始まりの行＋条件）。起動時の構成フェーズで再送する。</summary>
    private readonly Dictionary<string, IReadOnlyList<DebugBreakpoint>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ソースパス（絶対）→ 一時ブレークポイント行（1 始まり）。Run to Cursor で置き、次の停止で撤去する。
    /// 永続ブレークポイント（<see cref="_breakpoints"/>）と合わせて setBreakpoints で送る。</summary>
    private readonly Dictionary<string, HashSet<int>> _tempBreakpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>現在設定中の例外ブレークフィルタ ID。構成フェーズと即時反映で送る。</summary>
    private IReadOnlyList<string> _exceptionFilters = Array.Empty<string>();

    /// <summary>stackTrace/step の対象スレッド（停止時にその停止スレッドへ更新、UI からも切替可）。</summary>
    private int _activeThreadId;

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

    /// <summary>自然終了時のアダプタ後始末（<see cref="TearDownAdapterAsync"/>）を追跡するタスク。
    /// <see cref="WaitForIdleAsync"/> が待つ対象。null/完了済みなら後始末は残っていない。</summary>
    private Task? _teardownTask;

    public DebugSessionState State => _state;

    public bool IsAdapterAvailable => ExecutableResolver.IsOnPath(DebugAdapterCatalog.Netcoredbg.Executable);

    public bool SupportsSetVariable { get; private set; }
    public bool SupportsSetNextStatement { get; private set; }
    public bool SupportsStepInTargets { get; private set; }
    public IReadOnlyList<DebugExceptionFilter> ExceptionFilters { get; private set; } = Array.Empty<DebugExceptionFilter>();
    public int ActiveThreadId => _activeThreadId;

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
                env = config.Environment is { Count: > 0 } e ? e : null,
                stopAtEntry = config.StopAtEntry,
                justMyCode = config.JustMyCode,
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
                var caps = await client.SendRequestAsync("initialize", new
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
                CaptureCapabilities(caps);

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

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
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
        // セッション終了。次のセッションへ一時ブレークポイントを持ち越さない（永続分は記憶したまま再送する）。
        _tempBreakpoints.Clear();
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
                    _activeThreadId = threadId;
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
        // ここでは await しない（Finish は DAP イベント受信スレッドから呼ばれ、ここをブロックしたくない）が、
        // タスク自体は _teardownTask に残す。StateChanged で Terminated を見た直後に UI が「開始」を再度押して
        // ビルドが走ると、まだ生きているこのプロセスが dll/pdb を握ったままでビルドが「ファイル使用中」で
        // 失敗し得る（間欠的なデバッグ起動失敗の原因だった）。呼び出し元は再ビルド前に WaitForIdleAsync を待つ。
        if (client is not null) _teardownTask = TearDownAdapterAsync(client);
    }

    /// <summary>直前セッションの自然終了に伴うアダプタ後始末が完了するまで待つ。保留が無ければ即座に返る。</summary>
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_teardownTask is { } t)
        {
            try { await t.WaitAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* 後始末自体の失敗は無視（Dispose は例外を投げない設計） */ }
        }
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

    public async Task SetBreakpointsAsync(string sourcePath, IReadOnlyList<DebugBreakpoint> breakpoints, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(sourcePath);
        if (breakpoints.Count == 0) _breakpoints.Remove(path);
        else _breakpoints[path] = breakpoints;

        // 実行中（クライアント有り）なら即時反映。未起動なら構成フェーズで送られる。
        var client = _client;
        if (client is { IsRunning: true })
        {
            try { await PushSourceBreakpointsAsync(client, path, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Emit(DebugOutputCategory.Console, $"ブレークポイント設定に失敗: {ex.Message}"); }
        }
    }

    public async Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filterIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _exceptionFilters = filterIds;
        var client = _client;
        if (client is { IsRunning: true })
        {
            try { await SendExceptionBreakpointsAsync(client, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Emit(DebugOutputCategory.Console, $"例外ブレーク設定に失敗: {ex.Message}"); }
        }
    }

    public Task ContinueAsync(CancellationToken ct = default) => StepRequestAsync("continue", resumes: true, ct);
    public Task StepOverAsync(CancellationToken ct = default) => StepRequestAsync("next", resumes: true, ct);
    public Task StepInAsync(CancellationToken ct = default) => StepRequestAsync("stepIn", resumes: true, ct);
    public Task StepOutAsync(CancellationToken ct = default) => StepRequestAsync("stepOut", resumes: true, ct);

    public async Task PauseAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true } || _state != DebugSessionState.Running) return;
        // pause はスレッド指定が要る。アクティブが無ければ先頭スレッドを狙う。
        var threadId = _activeThreadId;
        if (threadId == 0)
        {
            var threads = await GetThreadsAsync(ct);
            threadId = threads.Count > 0 ? threads[0].Id : 1;
        }
        try { await client.SendRequestAsync("pause", new { threadId }, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"一時停止に失敗: {ex.Message}"); }
    }

    public void SetActiveThread(int threadId) => _activeThreadId = threadId;

    /// <summary>continue/next/stepIn/stepOut をアクティブスレッドに対して送る。送信成功で Running へ。</summary>
    private async Task StepRequestAsync(string command, bool resumes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true } || _state != DebugSessionState.Stopped) return;
        try
        {
            await client.SendRequestAsync(command, new { threadId = _activeThreadId }, ct);
            if (resumes)
            {
                SetState(DebugSessionState.Running);
                Continued?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"{command} に失敗: {ex.Message}"); }
    }

    public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(CancellationToken ct = default) => GetStackTraceAsync(_activeThreadId, ct);

    public async Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true } || _state != DebugSessionState.Stopped) return Array.Empty<DebugStackFrame>();
        try
        {
            var body = await client.SendRequestAsync("stackTrace",
                new { threadId, startFrame = 0, levels = 50 }, ct);
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
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DebugStackFrame>(); }
    }

    public async Task<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true }) return Array.Empty<DebugScope>();
        try
        {
            var body = await client.SendRequestAsync("scopes", new { frameId }, ct);
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
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DebugScope>(); }
    }

    public async Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true } || variablesReference <= 0) return Array.Empty<DebugVariable>();
        try
        {
            var body = await client.SendRequestAsync("variables", new { variablesReference }, ct);
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
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DebugVariable>(); }
    }

    public async Task<string> EvaluateAsync(string expression, int? frameId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true }) return "(セッションがありません)";
        try
        {
            var body = await client.SendRequestAsync("evaluate",
                new { expression, frameId, context = "watch" }, ct);
            if (body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("result", out var r))
                return r.GetString() ?? "";
            return "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"(評価エラー: {ex.Message})"; }
    }

    public async Task<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true }) return Array.Empty<DebugThread>();
        try
        {
            var body = await client.SendRequestAsync("threads", null, ct);
            var list = new List<DebugThread>();
            if (body is { ValueKind: JsonValueKind.Object } b &&
                b.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in threads.EnumerateArray())
                {
                    int id = t.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number ? idp.GetInt32() : 0;
                    var name = t.TryGetProperty("name", out var np) ? np.GetString() ?? $"Thread {id}" : $"Thread {id}";
                    list.Add(new DebugThread(id, name));
                }
            }
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DebugThread>(); }
    }

    public async Task<string?> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true } || variablesReference <= 0) return null;
        try
        {
            var body = await client.SendRequestAsync("setVariable", new { variablesReference, name, value }, ct);
            if (body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("value", out var r))
                return r.GetString() ?? value;
            return value;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"値の変更に失敗: {ex.Message}"); return null; }
    }

    public async Task<bool> SetNextStatementAsync(string sourcePath, int line, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true } || _state != DebugSessionState.Stopped) return false;
        try
        {
            var path = Path.GetFullPath(sourcePath);
            // まず gotoTargets でその行の有効なターゲット id を引き、goto で移動する。
            var targets = await client.SendRequestAsync("gotoTargets", new
            {
                source = new { path, name = Path.GetFileName(path) },
                line,
            }, ct);
            int targetId = -1;
            if (targets is { ValueKind: JsonValueKind.Object } tb &&
                tb.TryGetProperty("targets", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var first = arr[0];
                if (first.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number)
                    targetId = idp.GetInt32();
            }
            if (targetId < 0) { Emit(DebugOutputCategory.Console, "この行へは移動できません（有効なターゲットがありません）。"); return false; }

            await client.SendRequestAsync("goto", new { threadId = _activeThreadId, targetId }, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"次のステートメント設定に失敗: {ex.Message}"); return false; }
    }

    public async Task RunToCursorAsync(string sourcePath, int line, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true } || _state != DebugSessionState.Stopped) return;
        var path = Path.GetFullPath(sourcePath);
        if (!_tempBreakpoints.TryGetValue(path, out var set))
            _tempBreakpoints[path] = set = new HashSet<int>();
        set.Add(line);
        try
        {
            // 一時ブレークポイントを足して送り直してから続行する。次の停止で一時分は撤去される。
            await PushSourceBreakpointsAsync(client, path, ct);
            await client.SendRequestAsync("continue", new { threadId = _activeThreadId }, ct);
            SetState(DebugSessionState.Running);
            Continued?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"カーソル行まで実行に失敗: {ex.Message}"); }
    }

    public async Task<string> EvaluateReplAsync(string expression, int? frameId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true }) return "(セッションがありません)";
        try
        {
            var body = await client.SendRequestAsync("evaluate",
                new { expression, frameId, context = "repl" }, ct);
            if (body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("result", out var r))
                return r.GetString() ?? "";
            return "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"(評価エラー: {ex.Message})"; }
    }

    public async Task<IReadOnlyList<DebugModule>> GetModulesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true }) return Array.Empty<DebugModule>();
        try
        {
            var body = await client.SendRequestAsync("modules", new { startModule = 0, moduleCount = 0 }, ct);
            var list = new List<DebugModule>();
            if (body is { ValueKind: JsonValueKind.Object } b &&
                b.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mods.EnumerateArray())
                {
                    var name = m.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                    var mpath = m.TryGetProperty("path", out var pp) ? pp.GetString() : null;
                    var version = m.TryGetProperty("version", out var vp) ? vp.GetString() : null;
                    var symbols = m.TryGetProperty("symbolStatus", out var sp) ? sp.GetString() : null;
                    list.Add(new DebugModule(name, mpath, version, symbols));
                }
            }
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DebugModule>(); }
    }

    public async Task<IReadOnlyList<DebugStepInTarget>> GetStepInTargetsAsync(int frameId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true } || !SupportsStepInTargets) return Array.Empty<DebugStepInTarget>();
        try
        {
            var body = await client.SendRequestAsync("stepInTargets", new { frameId }, ct);
            var list = new List<DebugStepInTarget>();
            if (body is { ValueKind: JsonValueKind.Object } b &&
                b.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in targets.EnumerateArray())
                {
                    int id = t.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number ? idp.GetInt32() : -1;
                    if (id < 0) continue;
                    var label = t.TryGetProperty("label", out var lp) ? lp.GetString() ?? $"#{id}" : $"#{id}";
                    list.Add(new DebugStepInTarget(id, label));
                }
            }
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DebugStepInTarget>(); }
    }

    public async Task StepInTargetAsync(int targetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var client = _client;
        if (client is not { IsRunning: true } || _state != DebugSessionState.Stopped) return;
        try
        {
            await client.SendRequestAsync("stepIn", new { threadId = _activeThreadId, targetId }, ct);
            SetState(DebugSessionState.Running);
            Continued?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"特定の関数へのステップ インに失敗: {ex.Message}"); }
    }

    /// <summary>一時ブレークポイント（Run to Cursor 分）をすべて撤去し、影響ソースを送り直す。次の停止時に呼ぶ。</summary>
    private async Task ClearTempBreakpointsAsync()
    {
        if (_tempBreakpoints.Count == 0) return;
        var paths = _tempBreakpoints.Keys.ToList();
        _tempBreakpoints.Clear();
        var client = _client;
        if (client is not { IsRunning: true }) return;
        foreach (var p in paths)
        {
            try { await PushSourceBreakpointsAsync(client, p, CancellationToken.None); }
            catch { /* 1 ソースの失敗は他に波及させない */ }
        }
    }

    /// <summary>initialize 応答から対応機能と例外フィルタを取り出して保持する。</summary>
    private void CaptureCapabilities(JsonElement? caps)
    {
        SupportsSetVariable = false;
        SupportsSetNextStatement = false;
        SupportsStepInTargets = false;
        ExceptionFilters = Array.Empty<DebugExceptionFilter>();
        if (caps is not { ValueKind: JsonValueKind.Object } c) return;

        SupportsSetVariable = c.TryGetProperty("supportsSetVariable", out var sv) && sv.ValueKind == JsonValueKind.True;
        SupportsSetNextStatement = c.TryGetProperty("supportsGotoTargetsRequest", out var gt) && gt.ValueKind == JsonValueKind.True;
        SupportsStepInTargets = c.TryGetProperty("supportsStepInTargetsRequest", out var st) && st.ValueKind == JsonValueKind.True;

        if (c.TryGetProperty("exceptionBreakpointFilters", out var filters) && filters.ValueKind == JsonValueKind.Array)
        {
            var list = new List<DebugExceptionFilter>();
            foreach (var f in filters.EnumerateArray())
            {
                var id = f.TryGetProperty("filter", out var fp) ? fp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                var label = f.TryGetProperty("label", out var lp) ? lp.GetString() ?? id : id;
                var def = f.TryGetProperty("default", out var dp) && dp.ValueKind == JsonValueKind.True;
                list.Add(new DebugExceptionFilter(id, label, def));
            }
            ExceptionFilters = list;
        }
    }

    /// <summary>構成フェーズ：記憶している全ブレークポイント・例外ブレークを送り、最後に configurationDone を送る。</summary>
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

        foreach (var path in _breakpoints.Keys)
        {
            try { await PushSourceBreakpointsAsync(client, path, CancellationToken.None); }
            catch { /* 1 ソースの失敗は他に波及させない */ }
        }
        try { await SendExceptionBreakpointsAsync(client, CancellationToken.None); }
        catch { /* 例外フィルタ未対応でも続行 */ }
        try { await client.SendRequestAsync("configurationDone", null); }
        catch { /* netcoredbg は configurationDone 不要でも動くことがある */ }
    }

    /// <summary>あるソースの永続ブレークポイント（<see cref="_breakpoints"/>）と一時ブレークポイント
    /// （<see cref="_tempBreakpoints"/>＝Run to Cursor）を合わせて 1 回の setBreakpoints で送る。</summary>
    private Task PushSourceBreakpointsAsync(DapProtocolClient client, string path, CancellationToken ct)
    {
        var persistent = _breakpoints.TryGetValue(path, out var list)
            ? list : (IReadOnlyList<DebugBreakpoint>)Array.Empty<DebugBreakpoint>();
        var temp = _tempBreakpoints.TryGetValue(path, out var set) ? set : null;
        return SendBreakpointsAsync(client, path, persistent, temp, ct);
    }

    /// <summary>有効な永続行＋一時行を condition/hitCondition/logMessage 付きで送る（無効行は除外。
    /// 一時行は条件なし。永続行と重なる一時行は永続を優先して重複させない）。</summary>
    private static Task SendBreakpointsAsync(
        DapProtocolClient client, string path, IReadOnlyList<DebugBreakpoint> bps,
        IReadOnlyCollection<int>? tempLines, CancellationToken ct)
    {
        var items = bps.Where(b => b.Enabled).Select(b => new
        {
            line = b.Line,
            condition = string.IsNullOrWhiteSpace(b.Condition) ? null : b.Condition,
            hitCondition = string.IsNullOrWhiteSpace(b.HitCondition) ? null : b.HitCondition,
            logMessage = string.IsNullOrWhiteSpace(b.LogMessage) ? null : b.LogMessage,
        }).ToList();

        if (tempLines is { Count: > 0 })
        {
            var have = new HashSet<int>(items.Select(i => i.line));
            foreach (var l in tempLines)
                if (have.Add(l))
                    items.Add(new { line = l, condition = (string?)null, hitCondition = (string?)null, logMessage = (string?)null });
        }

        return client.SendRequestAsync("setBreakpoints", new
        {
            source = new { path, name = Path.GetFileName(path) },
            breakpoints = items.ToArray(),
        }, ct);
    }

    private Task SendExceptionBreakpointsAsync(DapProtocolClient client, CancellationToken ct)
        => client.SendRequestAsync("setExceptionBreakpoints", new { filters = _exceptionFilters.ToArray() }, ct);

    /// <summary>stopped の際、停止位置（ソース・行）を stackTrace から引いて通知する。</summary>
    private async Task HandleStoppedAsync(string reason, int threadId)
    {
        SetState(DebugSessionState.Stopped);
        // Run to Cursor の一時ブレークポイントは、どの理由で止まっても撤去する（VS と同じ）。
        await ClearTempBreakpointsAsync();
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
