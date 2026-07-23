using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.Services.Debug.Js;

/// <summary>
/// <see cref="IDebugService"/> の vscode-js-debug（Node.js / TypeScript）実装。netcoredbg（stdio・単一接続）と
/// 違い、js-debug は <b>TCP サーバ</b>（<see cref="JsDebugServerProcess"/>）に対して<b>親子 2 本の DAP 接続</b>を張る：
///
/// 1. 親接続で initialize → launch/attach（pwa-node）を送る。
/// 2. js-debug が親へ reverse request <c>startDebugging</c>（<c>__pendingTargetId</c> 入りの configuration）を送ってくる。
/// 3. 同じサーバへ<b>2 本目の TCP 接続</b>（子セッション）を張り、initialize → その configuration をそのまま
///    launch/attach として送る。<b>停止・スレッド・変数・ブレークポイントの実体は子セッション側</b>。
///
/// そのためデバッグ対象向けリクエスト（stackTrace / variables / setBreakpoints 等）は子接続があれば子へ、
/// まだ無ければ親へルーティングする。出力は親子両方から拾い、terminated はどちら由来でも終了処理へ集約する。
/// cluster / child_process による 2 個目以降の <c>startDebugging</c> は v1 では受理応答のみ（既知の制限、
/// コンソールに告知を出す）。セッション開始シーケンス（initialize → launch を await せず initialized で
/// configurationDone）は <see cref="NetcoredbgDebugService"/> と同じ流儀。
/// </summary>
public sealed class JsDebugService : IDebugService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsDebugServerProcess? _server;
    private TcpClient? _parentTcp;
    private DapConnection? _parent;
    private TcpClient? _childTcp;
    private DapConnection? _child;
    private int? _exitCode;
    private DebugSessionState _state = DebugSessionState.Idle;
    private bool _childStarted;

    /// <summary>ソースパス（絶対）→ ブレークポイント（1 始まりの行＋条件）。起動時の構成フェーズで再送する。</summary>
    private readonly Dictionary<string, IReadOnlyList<DebugBreakpoint>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ソースパス（絶対）→ 一時ブレークポイント行（1 始まり）。Run to Cursor で置き、次の停止で撤去する。</summary>
    private readonly Dictionary<string, HashSet<int>> _tempBreakpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>現在設定中の例外ブレークフィルタ ID（js-debug は all / uncaught）。</summary>
    private IReadOnlyList<string> _exceptionFilters = Array.Empty<string>();

    private int _activeThreadId;
    private bool _attached;

    /// <summary>親接続に launch/attach を書き終えたシグナル。構成フェーズはこれを待ってから configurationDone を
    /// 送る（netcoredbg と同じ順序保証。js-debug は寛容だが、同じ流儀で安全側に倒す）。</summary>
    private TaskCompletionSource? _parentRequestSent;

    /// <summary>子接続に launch/attach（startDebugging の configuration）を書き終えたシグナル。同上。</summary>
    private TaskCompletionSource? _childRequestSent;

    /// <summary>自然終了時の後始末（接続・サーバプロセスの破棄）を追跡するタスク。</summary>
    private Task? _teardownTask;

    public DebugSessionState State => _state;

    public bool IsAdapterAvailable => JsDebugAdapterLocator.IsInstalled;

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

    /// <summary>デバッグ対象向けリクエストの送り先。子（実体）が立っていれば子、まだなら親。</summary>
    private DapConnection? ActiveConn => _child ?? _parent;

    /// <summary>ブレークポイント送信の対象接続（親子両方。vscode も全セッションへ送る）。</summary>
    private IEnumerable<DapConnection> BreakpointConns()
    {
        if (_parent is { IsOpen: true } p) yield return p;
        if (_child is { IsOpen: true } c) yield return c;
    }

    public Task StartAsync(DebugLaunchConfig config, CancellationToken ct)
    {
        var workDir = config.WorkingDirectory
            ?? (TsLaunchTarget.TryParseNpmScript(config.Program, out _) ? null : Path.GetDirectoryName(config.Program))
            ?? Environment.CurrentDirectory;

        return BeginSessionAsync(
            requestCommand: "launch",
            buildArguments: () => BuildLaunchArguments(config, workDir),
            attaching: false,
            label: $"デバッグ起動: {config.Program}",
            failureLabel: "デバッグ起動に失敗しました",
            precheck: () =>
            {
                if (TsLaunchTarget.TryParseNpmScript(config.Program, out var script))
                    return string.IsNullOrWhiteSpace(script) ? "npm スクリプト名が空です。" : null;
                return File.Exists(config.Program) ? null : $"実行対象が見つかりません: {config.Program}";
            },
            ct: ct);
    }

    public Task AttachAsync(DebugAttachConfig config, CancellationToken ct)
    {
        var port = config.Port ?? 9229;
        return BeginSessionAsync(
            requestCommand: "attach",
            buildArguments: () => new
            {
                name = "Loomo TS Attach",
                type = "pwa-node",
                request = "attach",
                port,
                continueOnAttach = true,
            },
            attaching: true,
            label: $"アタッチ: 127.0.0.1:{port}",
            failureLabel: "アタッチに失敗しました",
            precheck: () => port is > 0 and < 65536 ? null : "デバッグポートが不正です。",
            ct: ct);
    }

    /// <summary>launch 引数を組み立てる。<c>npm:スクリプト名</c> は npm run へ、それ以外はファイル実行へ。
    /// TS は Node の型ストリッピング（23.6+）と js-debug のソースマップ解決に任せる。</summary>
    private static object BuildLaunchArguments(DebugLaunchConfig config, string workDir)
    {
        var skipFiles = config.JustMyCode ? new[] { "<node_internals>/**" } : Array.Empty<string>();
        var env = config.Environment is { Count: > 0 } e ? e : null;

        if (TsLaunchTarget.TryParseNpmScript(config.Program, out var script))
        {
            return new
            {
                name = "Loomo TS Debug",
                type = "pwa-node",
                request = "launch",
                cwd = workDir,
                runtimeExecutable = "npm",
                runtimeArgs = new[] { "run", script },
                env,
                stopOnEntry = config.StopAtEntry,
                skipFiles,
                console = "internalConsole",   // 出力を output イベントで受け取る
                sourceMaps = true,
            };
        }

        return new
        {
            name = "Loomo TS Debug",
            type = "pwa-node",
            request = "launch",
            program = config.Program,
            cwd = workDir,
            args = config.Args ?? Array.Empty<string>(),
            env,
            stopOnEntry = config.StopAtEntry,
            skipFiles,
            console = "internalConsole",
            sourceMaps = true,
        };
    }

    /// <summary>launch / attach に共通するセッション開始シーケンス（サーバ起動 → 親接続 → initialize →
    /// launch/attach 送信（await しない）→ initialized で構成フェーズ → response 完了で Running）。</summary>
    private async Task BeginSessionAsync(
        string requestCommand,
        Func<object> buildArguments,
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
                Emit(DebugOutputCategory.Important, JsDebugAdapterLocator.IsNodeInstalled
                    ? "デバッグアダプタ（vscode-js-debug）が未導入です。TS IDE ペインの構成タブからインストールしてください。"
                    : "Node.js（node）が PATH 上に見つかりません。Node.js をインストールしてください。");
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
            _childStarted = false;
            SetState(DebugSessionState.Launching);

            try
            {
                _server = await JsDebugServerProcess.StartAsync(Environment.CurrentDirectory, ct);
                _parentTcp = new TcpClient();
                await _parentTcp.ConnectAsync("127.0.0.1", _server.Port, ct);
                var stream = _parentTcp.GetStream();
                _parent = new DapConnection(stream, stream, label: "js-parent");
            }
            catch (Exception ex)
            {
                Emit(DebugOutputCategory.Important, $"デバッグアダプタの起動に失敗しました: {ex.Message}");
                SetState(DebugSessionState.Failed);
                await StopCoreAsync();
                return;
            }

            var parent = _parent;
            _parentRequestSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            parent.EventReceived += OnParentEvent;
            parent.Closed += OnParentClosed;
            parent.ReverseRequestHandler = OnParentReverseRequest;
            parent.Start();

            try
            {
                var caps = await parent.SendRequestAsync("initialize", new
                {
                    clientID = "loomo",
                    clientName = "Loomo",
                    adapterID = "js-debug",
                    locale = "ja",
                    linesStartAt1 = true,
                    columnsStartAt1 = true,
                    pathFormat = "path",
                    supportsRunInTerminalRequest = false,
                }, ct);
                CaptureCapabilities(caps);

                // launch/attach は configurationDone の後に response が返るため、ここでは await しない。
                var requestTask = parent.SendRequestAsync(requestCommand, buildArguments(), ct);
                _parentRequestSent.TrySetResult();

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

    /// <summary>親接続への reverse request。<c>startDebugging</c> が来たら子セッションを別 TCP 接続で開始する。
    /// DAP 読み取りスレッド上なのでブロックせず Task へ逃がす。</summary>
    private bool OnParentReverseRequest(string command, JsonElement args)
    {
        if (command != "startDebugging") return true;   // runInTerminal 等は internalConsole 運用なので来ない想定

        if (_childStarted)
        {
            // cluster / child_process の追加ターゲット。v1 では最初の 1 本だけデバッグする（既知の制限）。
            Emit(DebugOutputCategory.Console,
                "追加の子プロセスのデバッグ要求を受けましたが、現在は最初の 1 プロセスのみデバッグします。");
            return true;
        }
        _childStarted = true;

        // configuration と request 種別（launch/attach）を取り出して子セッションを開始する。
        string childRequest = args.ValueKind == JsonValueKind.Object &&
                              args.TryGetProperty("request", out var r) ? r.GetString() ?? "attach" : "attach";
        JsonElement config = args.ValueKind == JsonValueKind.Object &&
                             args.TryGetProperty("configuration", out var c) ? c.Clone() : default;
        _ = Task.Run(() => StartChildSessionAsync(childRequest, config));
        return true;
    }

    /// <summary>子セッション：同じサーバへ 2 本目の TCP 接続を張り、initialize → startDebugging の configuration を
    /// そのまま launch/attach として送る。停止イベント・ブレークポイントの実体はこちら側。</summary>
    private async Task StartChildSessionAsync(string requestCommand, JsonElement configuration)
    {
        var server = _server;
        if (server is not { IsRunning: true }) return;

        try
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", server.Port);
            var stream = tcp.GetStream();
            var child = new DapConnection(stream, stream, label: "js-child");
            _childTcp = tcp;
            _child = child;
            _childRequestSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            child.EventReceived += OnChildEvent;
            child.Closed += OnChildClosed;
            child.ReverseRequestHandler = OnParentReverseRequest;   // 孫の startDebugging も同じ制限告知
            child.Start();

            await child.SendRequestAsync("initialize", new
            {
                clientID = "loomo",
                clientName = "Loomo",
                adapterID = "js-debug",
                locale = "ja",
                linesStartAt1 = true,
                columnsStartAt1 = true,
                pathFormat = "path",
                supportsRunInTerminalRequest = false,
            }, CancellationToken.None);

            // launch/attach（configuration そのまま）。応答は configurationDone 後 —— await しない（親と同じ流儀）。
            object? argsObj = configuration.ValueKind == JsonValueKind.Object ? (object)configuration : new { };
            var requestTask = child.SendRequestAsync(requestCommand, argsObj, CancellationToken.None);
            _childRequestSent.TrySetResult();
            await requestTask;
        }
        catch (Exception ex)
        {
            Emit(DebugOutputCategory.Important, $"子デバッグセッションの開始に失敗しました: {ex.Message}");
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await StopCoreAsync();
            SetState(DebugSessionState.Idle);
        }
        finally { _gate.Release(); }
    }

    /// <summary>セッションを終了する（gate 取得済みの内部用）。親へ disconnect → 全接続とサーバを破棄。</summary>
    private async Task StopCoreAsync()
    {
        _parentRequestSent?.TrySetResult();
        _childRequestSent?.TrySetResult();
        _tempBreakpoints.Clear();

        var parent = _parent;
        var child = _child;
        var parentTcp = _parentTcp;
        var childTcp = _childTcp;
        var server = _server;
        _parent = null; _child = null; _parentTcp = null; _childTcp = null; _server = null;
        _childStarted = false;
        if (parent is null && server is null) return;

        if (parent is not null)
        {
            parent.EventReceived -= OnParentEvent;
            parent.Closed -= OnParentClosed;
        }
        if (child is not null)
        {
            child.EventReceived -= OnChildEvent;
            child.Closed -= OnChildClosed;
        }

        if (parent is { IsOpen: true })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                // アタッチ時は対象プロセスを巻き込まない（デタッチのみ）。launch 時は終了させる。
                await parent.SendRequestAsync("disconnect", new { terminateDebuggee = !_attached }, cts.Token);
                JsDebugLog.Write("StopCore: disconnect ok");
            }
            catch (Exception ex) { JsDebugLog.Write($"StopCore: disconnect failed: {ex.GetType().Name}"); }
        }

        JsDebugLog.Write("StopCore: disposing connections");
        child?.Dispose();
        parent?.Dispose();
        try { childTcp?.Dispose(); } catch { }
        try { parentTcp?.Dispose(); } catch { }
        JsDebugLog.Write("StopCore: disposing server");
        server?.Dispose();
        JsDebugLog.Write("StopCore: done");
    }

    private void OnParentEvent(string evt, JsonElement body) => OnDapEvent(evt, body, isChild: false);
    private void OnChildEvent(string evt, JsonElement body) => OnDapEvent(evt, body, isChild: true);

    private void OnDapEvent(string evt, JsonElement body, bool isChild)
    {
        switch (evt)
        {
            case "initialized":
                // 構成フェーズ：記憶しているブレークポイントを全ソース分送ってから configurationDone。
                _ = ConfigureAsync(isChild);
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
                        Emit(MapCategory(category), text!.TrimEnd('\n'));
                }
                break;

            case "exited":
                if (body.ValueKind == JsonValueKind.Object &&
                    body.TryGetProperty("exitCode", out var ec) && ec.ValueKind == JsonValueKind.Number)
                    _exitCode = ec.GetInt32();
                break;

            case "terminated":
                // 子（実体）の終了はセッション終了。親の terminated も同じ扱い。
                Finish($"terminated イベント（{(isChild ? "子" : "親")}）");
                break;
        }
    }

    private void OnParentClosed() => Finish("アダプタ接続の切断");
    private void OnChildClosed()
    {
        // 子接続が落ちた＝対象プロセス終了とみなす（親は生きていても対象はもう居ない）。
        Finish("子セッション接続の切断");
    }

    /// <summary>セッション終了の確定処理（イベント or 接続断から）。多重発火は状態で抑止。</summary>
    private void Finish(string reason)
    {
        if (_state is DebugSessionState.Terminated or DebugSessionState.Idle) return;
        var code = _exitCode;
        Emit(DebugOutputCategory.Important,
            $"デバッグ終了{(code is { } v ? $"（終了コード {v}）" : "")}");
        SetState(DebugSessionState.Terminated);
        Exited?.Invoke(this, new DebugExited(code, reason));

        // 接続とサーバプロセスの後始末（DAP イベント受信スレッドをブロックしない）。
        _teardownTask = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { await StopCoreAsync(); }
            finally { _gate.Release(); }
        });
    }

    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_teardownTask is { } t)
        {
            try { await t.WaitAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* 後始末自体の失敗は無視 */ }
        }
    }

    public async Task SetBreakpointsAsync(string sourcePath, IReadOnlyList<DebugBreakpoint> breakpoints, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(sourcePath);
        if (breakpoints.Count == 0) _breakpoints.Remove(path);
        else _breakpoints[path] = breakpoints;

        foreach (var conn in BreakpointConns())
        {
            try { await PushSourceBreakpointsAsync(conn, path, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Emit(DebugOutputCategory.Console, $"ブレークポイント設定に失敗: {ex.Message}"); }
        }
    }

    public async Task SetExceptionBreakpointsAsync(IReadOnlyList<string> filterIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _exceptionFilters = filterIds;
        foreach (var conn in BreakpointConns())
        {
            try { await SendExceptionBreakpointsAsync(conn, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Emit(DebugOutputCategory.Console, $"例外ブレーク設定に失敗: {ex.Message}"); }
        }
    }

    public Task ContinueAsync(CancellationToken ct = default) => StepRequestAsync("continue", ct);
    public Task StepOverAsync(CancellationToken ct = default) => StepRequestAsync("next", ct);
    public Task StepInAsync(CancellationToken ct = default) => StepRequestAsync("stepIn", ct);
    public Task StepOutAsync(CancellationToken ct = default) => StepRequestAsync("stepOut", ct);

    public async Task PauseAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var conn = ActiveConn;
        if (conn is not { IsOpen: true } || _state != DebugSessionState.Running) return;
        var threadId = _activeThreadId;
        if (threadId == 0)
        {
            var threads = await GetThreadsAsync(ct);
            threadId = threads.Count > 0 ? threads[0].Id : 1;
        }
        try { await conn.SendRequestAsync("pause", new { threadId }, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"一時停止に失敗: {ex.Message}"); }
    }

    public void SetActiveThread(int threadId) => _activeThreadId = threadId;

    private async Task StepRequestAsync(string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var conn = ActiveConn;
        if (conn is not { IsOpen: true } || _state != DebugSessionState.Stopped) return;
        try
        {
            await conn.SendRequestAsync(command, new { threadId = _activeThreadId }, ct);
            SetState(DebugSessionState.Running);
            Continued?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"{command} に失敗: {ex.Message}"); }
    }

    public Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(CancellationToken ct = default)
        => GetStackTraceAsync(_activeThreadId, ct);

    public async Task<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var conn = ActiveConn;
        if (conn is not { IsOpen: true } || _state != DebugSessionState.Stopped) return Array.Empty<DebugStackFrame>();
        try
        {
            var body = await conn.SendRequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 50 }, ct);
            return DapModelParser.ParseStackFrames(body);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DebugStackFrame>(); }
    }

    public async Task<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var conn = ActiveConn;
        if (conn is not { IsOpen: true }) return Array.Empty<DebugScope>();
        try
        {
            var body = await conn.SendRequestAsync("scopes", new { frameId }, ct);
            return DapModelParser.ParseScopes(body);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DebugScope>(); }
    }

    public async Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var conn = ActiveConn;
        if (conn is not { IsOpen: true } || variablesReference <= 0) return Array.Empty<DebugVariable>();
        try
        {
            var body = await conn.SendRequestAsync("variables", new { variablesReference }, ct);
            return DapModelParser.ParseVariables(body);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DebugVariable>(); }
    }

    public Task<string> EvaluateAsync(string expression, int? frameId, CancellationToken ct = default)
        => EvaluateCoreAsync(expression, frameId, "watch", ct);

    public Task<string> EvaluateReplAsync(string expression, int? frameId, CancellationToken ct = default)
        => EvaluateCoreAsync(expression, frameId, "repl", ct);

    private async Task<string> EvaluateCoreAsync(string expression, int? frameId, string context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var conn = ActiveConn;
        if (conn is not { IsOpen: true }) return "(セッションがありません)";
        try
        {
            var body = await conn.SendRequestAsync("evaluate", new { expression, frameId, context }, ct);
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
        var conn = ActiveConn;
        if (conn is not { IsOpen: true }) return Array.Empty<DebugThread>();
        try
        {
            var body = await conn.SendRequestAsync("threads", null, ct);
            return DapModelParser.ParseThreads(body);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<DebugThread>(); }
    }

    public async Task<string?> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var conn = ActiveConn;
        if (conn is not { IsOpen: true } || variablesReference <= 0) return null;
        try
        {
            var body = await conn.SendRequestAsync("setVariable", new { variablesReference, name, value }, ct);
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
        var conn = ActiveConn;
        if (conn is not { IsOpen: true } || _state != DebugSessionState.Stopped || !SupportsSetNextStatement) return false;
        try
        {
            var path = Path.GetFullPath(sourcePath);
            var targets = await conn.SendRequestAsync("gotoTargets", new
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

            await conn.SendRequestAsync("goto", new { threadId = _activeThreadId, targetId }, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"次のステートメント設定に失敗: {ex.Message}"); return false; }
    }

    public async Task RunToCursorAsync(string sourcePath, int line, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var conn = ActiveConn;
        if (conn is not { IsOpen: true } || _state != DebugSessionState.Stopped) return;
        var path = Path.GetFullPath(sourcePath);
        if (!_tempBreakpoints.TryGetValue(path, out var set))
            _tempBreakpoints[path] = set = new HashSet<int>();
        set.Add(line);
        try
        {
            // 一時ブレークポイントを足して送り直してから続行する。次の停止で一時分は撤去される。
            await PushSourceBreakpointsAsync(conn, path, ct);
            await conn.SendRequestAsync("continue", new { threadId = _activeThreadId }, ct);
            SetState(DebugSessionState.Running);
            Continued?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Emit(DebugOutputCategory.Console, $"カーソル行まで実行に失敗: {ex.Message}"); }
    }

    public async Task<IReadOnlyList<DebugModule>> GetModulesAsync(CancellationToken ct = default)
    {
        // js-debug は modules リクエスト非対応（loadedSources 系）。TS ペインにモジュールタブは無い。
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        return Array.Empty<DebugModule>();
    }

    public async Task<IReadOnlyList<DebugStepInTarget>> GetStepInTargetsAsync(int frameId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var conn = ActiveConn;
        if (conn is not { IsOpen: true } || !SupportsStepInTargets) return Array.Empty<DebugStepInTarget>();
        try
        {
            var body = await conn.SendRequestAsync("stepInTargets", new { frameId }, ct);
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
        var conn = ActiveConn;
        if (conn is not { IsOpen: true } || _state != DebugSessionState.Stopped) return;
        try
        {
            await conn.SendRequestAsync("stepIn", new { threadId = _activeThreadId, targetId }, ct);
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
        foreach (var conn in BreakpointConns())
        {
            foreach (var p in paths)
            {
                try { await PushSourceBreakpointsAsync(conn, p, CancellationToken.None); }
                catch { /* 1 ソースの失敗は他に波及させない */ }
            }
        }
    }

    /// <summary>initialize 応答から対応機能と例外フィルタを取り出して保持する。</summary>
    private void CaptureCapabilities(JsonElement? caps)
    {
        var (sv, gt, st, filters) = DapModelParser.ParseCapabilities(caps);
        SupportsSetVariable = sv;
        SupportsSetNextStatement = gt;
        SupportsStepInTargets = st;
        ExceptionFilters = filters;
    }

    /// <summary>構成フェーズ：記憶している全ブレークポイント・例外ブレークを送り、最後に configurationDone を送る。
    /// 親・子どちらの initialized からも呼ばれる（それぞれ自分の接続に対して行う）。</summary>
    private async Task ConfigureAsync(bool isChild)
    {
        var conn = isChild ? _child : _parent;
        if (conn is null) return;

        // launch/attach が書かれるまで待つ（順序保証。netcoredbg と同じ流儀）。
        var requestSent = isChild ? _childRequestSent : _parentRequestSent;
        if (requestSent is not null)
        {
            try { await requestSent.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { /* タイムアウトでも configurationDone は送る */ }
        }

        foreach (var path in _breakpoints.Keys.ToList())
        {
            try { await PushSourceBreakpointsAsync(conn, path, CancellationToken.None); }
            catch { /* 1 ソースの失敗は他に波及させない */ }
        }
        try { await SendExceptionBreakpointsAsync(conn, CancellationToken.None); }
        catch { /* 例外フィルタ未対応でも続行 */ }
        try { await conn.SendRequestAsync("configurationDone", null); }
        catch { /* 送れなくても続行 */ }
    }

    /// <summary>あるソースの永続＋一時ブレークポイントを合わせて 1 回の setBreakpoints で送る。</summary>
    private Task PushSourceBreakpointsAsync(DapConnection conn, string path, CancellationToken ct)
    {
        var persistent = _breakpoints.TryGetValue(path, out var list)
            ? list : (IReadOnlyList<DebugBreakpoint>)Array.Empty<DebugBreakpoint>();
        var temp = _tempBreakpoints.TryGetValue(path, out var set) ? set : null;

        var items = persistent.Where(b => b.Enabled).Select(b => new
        {
            line = b.Line,
            condition = string.IsNullOrWhiteSpace(b.Condition) ? null : b.Condition,
            hitCondition = string.IsNullOrWhiteSpace(b.HitCondition) ? null : b.HitCondition,
            logMessage = string.IsNullOrWhiteSpace(b.LogMessage) ? null : b.LogMessage,
        }).ToList();

        if (temp is { Count: > 0 })
        {
            var have = new HashSet<int>(items.Select(i => i.line));
            foreach (var l in temp)
                if (have.Add(l))
                    items.Add(new { line = l, condition = (string?)null, hitCondition = (string?)null, logMessage = (string?)null });
        }

        return conn.SendRequestAsync("setBreakpoints", new
        {
            source = new { path, name = Path.GetFileName(path) },
            breakpoints = items.ToArray(),
        }, ct);
    }

    private Task SendExceptionBreakpointsAsync(DapConnection conn, CancellationToken ct)
        => conn.SendRequestAsync("setExceptionBreakpoints", new { filters = _exceptionFilters.ToArray() }, ct);

    /// <summary>stopped の際、停止位置（ソース・行）を stackTrace から引いて通知する。</summary>
    private async Task HandleStoppedAsync(string reason, int threadId)
    {
        SetState(DebugSessionState.Stopped);
        await ClearTempBreakpointsAsync();
        string? sourcePath = null;
        int line = 0;
        try
        {
            var conn = ActiveConn;
            if (conn is { IsOpen: true })
            {
                var body = await conn.SendRequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 1 });
                var frames = DapModelParser.ParseStackFrames(body);
                if (frames.Count > 0)
                {
                    sourcePath = frames[0].SourcePath;
                    line = frames[0].Line;
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
