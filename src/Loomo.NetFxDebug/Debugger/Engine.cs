using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ClrDebug;
using Microsoft.Win32.SafeHandles;
using sk0ya.Loomo.NetFxDebug.Dap;

namespace sk0ya.Loomo.NetFxDebug.Debugger;

/// <summary>読み込まれたモジュール 1 つ。</summary>
internal sealed class ModuleEntry
{
    public ModuleEntry(CorDebugModule module, string path)
    {
        Module = module;
        Path = path;
        BaseAddress = module.BaseAddress.Value;
    }

    public CorDebugModule Module { get; }
    public string Path { get; }
    public ulong BaseAddress { get; }
    public ModuleMetadata? Metadata { get; set; }
    public ModuleSymbols? Symbols { get; set; }
    public int Id { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>ソースに置かれたブレークポイント 1 つ（DAP の id を持つ）。</summary>
internal sealed class SourceBreakpoint
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string? Condition { get; set; }
    public string? HitCondition { get; set; }
    public string? LogMessage { get; set; }
    public int HitCount { get; set; }
    public int? ActualLine { get; set; }
    public List<BoundBreakpoint> Bound { get; } = new();
    public bool IsTemporaryEntry { get; set; }
}

internal sealed class BoundBreakpoint
{
    public BoundBreakpoint(ModuleEntry module, int methodToken, int offset, CorDebugFunctionBreakpoint breakpoint)
    {
        Module = module;
        MethodToken = methodToken;
        Offset = offset;
        Breakpoint = breakpoint;
    }

    public ModuleEntry Module { get; }
    public int MethodToken { get; }
    public int Offset { get; }
    public CorDebugFunctionBreakpoint Breakpoint { get; }
}

/// <summary>停止中スレッドのフレーム 1 つ（IL フレームだけを扱う）。</summary>
internal sealed class FrameInfo
{
    public FrameInfo(CorDebugILFrame frame, ModuleEntry? module, int methodToken, int ilOffset, bool isLeaf)
    {
        Frame = frame;
        Module = module;
        MethodToken = methodToken;
        ILOffset = ilOffset;
        IsLeaf = isLeaf;
    }

    public CorDebugILFrame Frame { get; }
    public ModuleEntry? Module { get; }
    public int MethodToken { get; }
    public int ILOffset { get; }
    public bool IsLeaf { get; }

    /// <summary>このフレームの行。呼び出し元フレームの IP は「呼び出しの次の命令」なので 1 つ手前で引く。</summary>
    public SequencePointInfo? Location
        => Module?.Symbols?.Method(MethodToken)?.VisibleAt(IsLeaf ? ILOffset : Math.Max(0, ILOffset - 1));
}

internal enum StepKind { Over, In, Out }

/// <summary>
/// ICorDebug で .NET Framework のプロセスを動かす本体。
///
/// <para><b>スレッドの約束</b>：ICorDebug を触るのは必ず <see cref="Sync"/> を握ったとき。ICorDebug のコールバックは
/// 専用スレッドで来て、そのあいだ対象プロセスは止まっている（同期状態）。コールバックから戻る前に Continue しなければ
/// 止まったまま——ブレークポイントで止めるのはこれを利用する。関数評価（プロパティの getter や ToString）は
/// 対象を一度走らせて EvalComplete コールバックを待つので、待つ側は <see cref="Monitor.Wait(object)"/> でロックを
/// 手放す。評価のあいだに他の要求が ICorDebug を触らないよう、評価中は <see cref="WaitIdle"/> で待たせる。</para>
///
/// <para><b>値の寿命</b>：対象を 1 度でも走らせると（評価も含む）フレームと値のオブジェクトは無効になる。
/// だから走らせるたびに <see cref="Generation"/> を進め、値は「どこから辿ったか」（<see cref="ValueSource"/>）
/// で持って世代が変わったら辿り直す。ヒープのオブジェクトは強いハンドルにして辿り直しを省く。</para>
/// </summary>
internal sealed partial class Engine
{
    public readonly object Sync = new();

    private readonly DapChannel _dap;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Dictionary<ulong, ModuleEntry> _modules = new();
    private readonly Dictionary<string, List<SourceBreakpoint>> _sourceBreakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CorDebugHandleValue> _handles = new();
    private readonly ManualResetEventSlim _exited = new(false);

    private CorDebug? _cor;
    private CorDebugProcess? _process;
    private Process? _osProcess;
    private bool _stopped;
    private bool _configured;
    private bool _launching;
    private bool _stopAtEntry;
    private bool _justMyCode = true;
    private bool _breakOnAll;
    private bool _breakOnUserUnhandled;
    private int _nextBreakpointId;
    private int _nextModuleId;
    private int _generation;
    private int _stoppedThreadId;
    private CorDebugStepper? _stepper;
    private StepKind _stepKind;
    private int _stepThreadId;
    private int _restepCount;
    private SourceBreakpoint? _entryBreakpoint;
    private string? _programPath;

    public Engine(DapChannel dap)
    {
        _dap = dap;
        var worker = new Thread(() =>
        {
            foreach (var action in _work.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { Log("内部エラー: " + ex.Message); }
            }
        }) { IsBackground = true, Name = "NetFxDebug worker" };
        worker.Start();
    }

    /// <summary>対象を走らせるたびに進む。フレームと値はこれが変わったら辿り直す。</summary>
    public int Generation => _generation;

    public bool IsStopped => _stopped;

    /// <summary>実行中のプロセスへアタッチしたセッションか（launch ではない）。</summary>
    public bool IsAttached => _process is not null && !_launching;

    public int StoppedThreadId => _stoppedThreadId;

    public bool JustMyCode => _justMyCode;

    public CorDebugProcess? Process => _process;

    public WaitHandle ExitedHandle => _exited.WaitHandle;

    public void Log(string text) => _dap.Output(text, "console");

    // ---------------------------------------------------------------- 起動・接続・終了

    /// <summary>対象を起動する。プロセスは CreateProcess コールバックで止めておき、構成（ブレークポイント送信）が
    /// 終わった <see cref="ConfigurationDone"/> で走らせる——起動直後のブレークポイントを取りこぼさないため。</summary>
    public void Launch(string program, string[] args, string? cwd, IReadOnlyDictionary<string, string>? env,
        bool stopAtEntry, bool justMyCode)
    {
        lock (Sync)
        {
            _programPath = Path.GetFullPath(program);
            _stopAtEntry = stopAtEntry;
            _justMyCode = justMyCode;
            _launching = true;
            _cor = CreateCorDebugForFile(_programPath);

            var commandLine = QuoteArgument(_programPath) +
                string.Concat(args.Select(a => " " + QuoteArgument(a)));
            var stdout = CreateOutputPipe(out var stdoutWrite);
            var stderr = CreateOutputPipe(out var stderrWrite);
            var environment = IntPtr.Zero;
            try
            {
                var startup = new STARTUPINFOW
                {
                    cb = Marshal.SizeOf<STARTUPINFOW>(),
                    dwFlags = STARTF.STARTF_USESTDHANDLES,
                    hStdInput = IntPtr.Zero,
                    hStdOutput = stdoutWrite,
                    hStdError = stderrWrite,
                };
                var flags = CreateProcessFlags.CREATE_NO_WINDOW;
                if (env is { Count: > 0 })
                {
                    environment = BuildEnvironmentBlock(env);
                    flags |= CreateProcessFlags.CREATE_UNICODE_ENVIRONMENT;
                }
                var information = new PROCESS_INFORMATION();
                _process = _cor.CreateProcess(null, commandLine, new SECURITY_ATTRIBUTES(), new SECURITY_ATTRIBUTES(),
                    true, flags, environment, string.IsNullOrWhiteSpace(cwd) ? Path.GetDirectoryName(_programPath) : cwd,
                    startup, ref information, CorDebugCreateProcessFlags.DEBUG_NO_SPECIAL_OPTIONS);
                if (information.hProcess != IntPtr.Zero) CloseHandle(information.hProcess);
                if (information.hThread != IntPtr.Zero) CloseHandle(information.hThread);
                _osProcess = OpenOsProcess(_process.Id);
            }
            finally
            {
                CloseHandle(stdoutWrite);
                CloseHandle(stderrWrite);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            }
            PumpOutput(stdout, "stdout");
            PumpOutput(stderr, "stderr");
        }
    }

    /// <summary>実行中の .NET Framework プロセスに接続する。</summary>
    public void Attach(int processId, bool justMyCode)
    {
        lock (Sync)
        {
            _configured = true;
            _justMyCode = justMyCode;
            _cor = CreateCorDebugForProcess(processId);
            _process = _cor.DebugActiveProcess(processId, false);
            _osProcess = OpenOsProcess(processId);
        }
    }

    /// <summary>構成フェーズの終わり。起動時に止めておいたプロセスを走らせる。</summary>
    public void ConfigurationDone()
    {
        lock (Sync)
        {
            _configured = true;
            if (_launching && _stopped) Resume(sendContinued: false);
        }
    }

    /// <summary>終了：launch なら対象を終わらせ、attach なら切り離す。</summary>
    public void Disconnect(bool terminateDebuggee)
    {
        CorDebugProcess? process;
        lock (Sync)
        {
            process = _process;
            if (process is null || _exited.IsSet) return;
        }

        if (terminateDebuggee)
        {
            try { process.Terminate(1); }
            catch (Exception ex) when (ex is COMException or DebugException) { }
            _exited.Wait(TimeSpan.FromSeconds(5));
            if (!_exited.IsSet)
            {
                try { _osProcess?.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return;
        }

        SynchronizedDo(() =>
        {
            _stepper?.TryDeactivate();
            _stepper = null;
            foreach (var bound in _sourceBreakpoints.Values.SelectMany(l => l).SelectMany(b => b.Bound))
                bound.Breakpoint.TryActivate(false);
            DisposeHandles();
            _stopped = false;
            try { process.Detach(); }
            catch (Exception ex) when (ex is COMException or DebugException) { Log("切り離しに失敗しました: " + ex.Message); }
            return true;
        }, continueAfter: false);
        _exited.Set();
    }

    /// <summary>OS のプロセスを開いてハンドルを握っておく（終了した後でも終了コードを読めるように）。</summary>
    private static Process? OpenOsProcess(int processId)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(processId);
            _ = process.Handle;
            return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>ICorDebug を後始末する（アダプタ終了時）。</summary>
    public void Shutdown()
    {
        _work.CompleteAdding();
        try { _cor?.TryTerminate(); } catch (Exception ex) when (ex is COMException or DebugException) { }
    }

    private CorDebug CreateCorDebugForFile(string program)
    {
        var metaHost = new CLRMetaHost();
        string version;
        try { version = metaHost.GetVersionFromFile(program); }
        catch (Exception ex) when (ex is COMException or DebugException)
        {
            throw new InvalidOperationException($".NET Framework のアセンブリとして読めません: {program}");
        }
        CLRRuntimeInfo runtime;
        try { runtime = metaHost.GetRuntime(version); }
        catch (Exception ex) when (ex is COMException or DebugException)
        {
            throw new InvalidOperationException($"対象が必要とする .NET Framework のランタイム（{version}）がインストールされていません。");
        }
        return InitializeCorDebug(runtime);
    }

    private CorDebug CreateCorDebugForProcess(int processId)
    {
        var metaHost = new CLRMetaHost();
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        var runtimes = metaHost.EnumerateLoadedRuntimes(process.Handle);
        var loaded = new List<CLRRuntimeInfo>();
        foreach (var item in runtimes) loaded.Add(new CLRRuntimeInfo((ICLRRuntimeInfo)item));
        if (loaded.Count == 0)
            throw new InvalidOperationException("このプロセスには .NET Framework のランタイムが読み込まれていません。");
        return InitializeCorDebug(loaded.OrderByDescending(r => r.VersionString).First());
    }

    private CorDebug InitializeCorDebug(CLRRuntimeInfo runtime)
    {
        var cor = runtime.GetInterface<CorDebug>(Extensions.CLSID_CLRDebuggingLegacy);
        cor.Initialize();
        cor.SetManagedHandler(CreateCallback());
        return cor;
    }

    // ---------------------------------------------------------------- 実行制御

    /// <summary>止まっている対象を走らせる（ロック内で呼ぶ）。</summary>
    public void Resume(bool sendContinued)
    {
        if (!_stopped || _process is null) return;
        _stopped = false;
        _generation++;
        ClearStopState();
        try { _process.Continue(false); }
        catch (Exception ex) when (ex is COMException or DebugException) { Log("続行に失敗しました: " + ex.Message); }
        if (sendContinued) _dap.SendEvent("continued", new { threadId = _stoppedThreadId, allThreadsContinued = true });
    }

    public void Continue()
    {
        lock (Sync)
        {
            WaitIdle();
            Resume(sendContinued: false);
        }
    }

    /// <summary>「すべて中断」。走っている対象を同期させ、管理コードを実行中のスレッドを停止スレッドとして報告する。</summary>
    public void Pause()
    {
        CorDebugProcess? process;
        lock (Sync)
        {
            WaitIdle();
            if (_stopped || _process is null) return;
            process = _process;
        }
        try { process.Stop(0); }
        catch (Exception ex) when (ex is COMException or DebugException) { Log("中断に失敗しました: " + ex.Message); return; }
        lock (Sync)
        {
            if (_stopped)
            {
                // Stop を呼ぶあいだに別の理由（ブレークポイント等）で止まった。二重になった分を戻す。
                try { process.Continue(false); } catch (Exception ex) when (ex is COMException or DebugException) { }
                return;
            }
            _stopped = true;
            _generation++;
            var thread = PickPauseThread(process);
            ReportStopped("pause", thread);
        }
    }

    private int PickPauseThread(CorDebugProcess process)
    {
        CorDebugThread? fallback = null;
        foreach (var thread in process.Threads)
        {
            fallback ??= thread;
            try
            {
                if (WalkFrames(thread).Any(f => f.Module?.Symbols is not null)) return thread.Id;
            }
            catch (Exception ex) when (ex is COMException or DebugException) { }
        }
        return fallback?.Id ?? 0;
    }

    public void Step(int threadId, StepKind kind)
    {
        lock (Sync)
        {
            WaitIdle();
            if (!_stopped || _process is null) return;
            var thread = _process.GetThread(threadId);
            _restepCount = 0;
            if (!BeginStep(thread, kind)) return;
            Resume(sendContinued: false);
        }
    }

    /// <summary>ステッパーを作って仕掛ける。今の行の範囲をまたいで進む（隠し区間は範囲に含める）。
    /// JMC（マイコードのみ）ステッパーにして、シンボルの無いコード（フレームワーク）には入らない。</summary>
    private bool BeginStep(CorDebugThread thread, StepKind kind)
    {
        try
        {
            _stepper?.TryDeactivate();
            var frames = WalkFrames(thread);
            var top = frames.FirstOrDefault();
            var stepper = top is not null ? top.Frame.CreateStepper() : thread.CreateStepper();
            stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
            stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
            stepper.SetJMC(true);
            if (kind == StepKind.Out)
            {
                stepper.StepOut();
            }
            else
            {
                var range = top?.Module?.Symbols?.Method(top.MethodToken)?.StepRange(top.ILOffset);
                if (range is { } r)
                {
                    stepper.SetRangeIL(true);
                    stepper.StepRange(kind == StepKind.In,
                        new[] { new COR_DEBUG_STEP_RANGE { startOffset = r.Start, endOffset = r.End } }, 1);
                }
                else
                {
                    stepper.Step(kind == StepKind.In);
                }
            }
            _stepper = stepper;
            _stepKind = kind;
            _stepThreadId = thread.Id;
            return true;
        }
        catch (Exception ex) when (ex is COMException or DebugException)
        {
            Log("ステップを開始できませんでした: " + ex.Message);
            return false;
        }
    }

    /// <summary>次のステートメントの設定：今のメソッド内の指定行へ IP を移す。</summary>
    public bool SetNextStatement(int threadId, int line, out string? error)
    {
        lock (Sync)
        {
            WaitIdle();
            error = null;
            if (!_stopped || _process is null) { error = "停止していません。"; return false; }
            var frame = WalkFrames(_process.GetThread(threadId)).FirstOrDefault();
            var point = frame?.Module?.Symbols?.PointOnLine(frame.MethodToken, line);
            if (frame is null || point is null) { error = "この行は現在のメソッドの中にありません。"; return false; }
            try
            {
                frame.Frame.CanSetIP(point.Offset);
                frame.Frame.SetIP(point.Offset);
                _generation++;
                ClearStopState(keepHandles: true);
                return true;
            }
            catch (Exception ex) when (ex is COMException or DebugException)
            {
                error = "この行へは移動できません: " + ex.Message;
                return false;
            }
        }
    }

    // ---------------------------------------------------------------- ブレークポイント

    /// <summary>1 ソース分のブレークポイントを置き直す。戻り値は DAP の breakpoints（id・verified・実際の行）。</summary>
    public IReadOnlyList<SourceBreakpoint> SetBreakpoints(string path, IReadOnlyList<SourceBreakpoint> requested)
    {
        var key = ModuleSymbols.NormalizePath(path);
        return SynchronizedDo(() =>
        {
            if (_sourceBreakpoints.TryGetValue(key, out var previous))
                foreach (var bound in previous.SelectMany(b => b.Bound))
                    bound.Breakpoint.TryActivate(false);
            foreach (var breakpoint in requested)
            {
                breakpoint.Id = ++_nextBreakpointId;
                breakpoint.Path = key;
                foreach (var module in _modules.Values) Bind(breakpoint, module);
            }
            _sourceBreakpoints[key] = requested.ToList();
            return requested;
        }, continueAfter: true)!;
    }

    public void SetExceptionFilters(IReadOnlyCollection<string> filters)
    {
        lock (Sync)
        {
            _breakOnAll = filters.Contains("all");
            _breakOnUserUnhandled = filters.Contains("user-unhandled");
        }
    }

    private void Bind(SourceBreakpoint breakpoint, ModuleEntry module)
    {
        if (module.Symbols?.Resolve(breakpoint.Path, breakpoint.Line) is not { } location) return;
        if (breakpoint.Bound.Any(b => b.Module == module)) return;
        try
        {
            var function = module.Module.GetFunctionFromToken(location.Method.Token);
            var bp = function.ILCode.CreateBreakpoint(location.Point.Offset);
            bp.Activate(true);
            breakpoint.Bound.Add(new BoundBreakpoint(module, location.Method.Token, location.Point.Offset, bp));
            var changed = breakpoint.ActualLine != location.Point.StartLine;
            breakpoint.ActualLine = location.Point.StartLine;
            if (changed && _configured && !breakpoint.IsTemporaryEntry)
                _dap.SendEvent("breakpoint", new
                {
                    reason = "changed",
                    breakpoint = new { id = breakpoint.Id, verified = true, line = breakpoint.ActualLine },
                });
        }
        catch (Exception ex) when (ex is COMException or DebugException)
        {
            Log($"ブレークポイントを置けませんでした（{Path.GetFileName(breakpoint.Path)}:{breakpoint.Line}）: {ex.Message}");
        }
    }

    private SourceBreakpoint? FindBreakpoint(CorDebugBreakpoint hit)
    {
        if (hit is not CorDebugFunctionBreakpoint function && hit.Raw is ICorDebugFunctionBreakpoint raw)
            function = new CorDebugFunctionBreakpoint(raw);
        else if (hit is CorDebugFunctionBreakpoint direct)
            function = direct;
        else
            return null;

        int token, offset;
        ulong module;
        try
        {
            token = function.Function.Token;
            offset = function.Offset;
            module = function.Function.Module.BaseAddress.Value;
        }
        catch (Exception ex) when (ex is COMException or DebugException) { return null; }

        bool Matches(SourceBreakpoint b) => b.Bound.Any(x =>
            x.MethodToken == token && x.Offset == offset && x.Module.BaseAddress == module);
        if (_entryBreakpoint is not null && Matches(_entryBreakpoint)) return _entryBreakpoint;
        return _sourceBreakpoints.Values.SelectMany(l => l).FirstOrDefault(Matches);
    }

    /// <summary>対象を一時的に同期させて <paramref name="action"/> を実行する。止まっていればそのまま。
    /// 走っていれば Stop → 実行 → Continue（<paramref name="continueAfter"/>）。</summary>
    private T? SynchronizedDo<T>(Func<T> action, bool continueAfter) where T : class
    {
        CorDebugProcess? process;
        lock (Sync)
        {
            WaitIdle();
            process = _process;
            if (process is null || _stopped || _launching && !_configured) return action();
        }
        try { process.Stop(0); }
        catch (Exception ex) when (ex is COMException or DebugException)
        {
            lock (Sync) return action();
        }
        lock (Sync)
        {
            try { return action(); }
            finally
            {
                if (continueAfter && !_exited.IsSet)
                {
                    try { process.Continue(false); }
                    catch (Exception ex) when (ex is COMException or DebugException) { }
                }
            }
        }
    }

    private void SynchronizedDo(Func<bool> action, bool continueAfter) => SynchronizedDo<object>(() => action(), continueAfter);

    // ---------------------------------------------------------------- スレッドとフレーム

    public IReadOnlyList<(int Id, string Name)> Threads()
    {
        lock (Sync)
        {
            WaitIdle();
            if (_process is null) return Array.Empty<(int, string)>();
            var list = new List<(int, string)>();
            var process = _process;
            var wasStopped = _stopped;
            if (!wasStopped)
            {
                // 走っている間はスレッド一覧だけ返す（名前を読むには同期が要る）。
                try { foreach (var thread in process.Threads) list.Add((thread.Id, $"スレッド {thread.Id}")); }
                catch (Exception ex) when (ex is COMException or DebugException) { }
                return list;
            }
            foreach (var thread in process.Threads)
            {
                string name;
                try { name = ThreadName(thread) ?? $"スレッド {thread.Id}"; }
                catch (Exception ex) when (ex is COMException or DebugException) { name = $"スレッド {thread.Id}"; }
                list.Add((thread.Id, name));
            }
            return list;
        }
    }

    private readonly Dictionary<int, (int Generation, List<FrameInfo> Frames)> _frameCache = new();

    /// <summary>スレッドの IL フレームを葉から順に。世代ごとにキャッシュする（評価で走らせたら辿り直す）。</summary>
    public List<FrameInfo> WalkFrames(CorDebugThread thread)
    {
        if (_frameCache.TryGetValue(thread.Id, out var cached) && cached.Generation == _generation)
            return cached.Frames;
        var frames = new List<FrameInfo>();
        foreach (var chain in thread.Chains)
        {
            bool managed;
            try { managed = chain.IsManaged; }
            catch (Exception ex) when (ex is COMException or DebugException) { continue; }
            if (!managed) continue;
            foreach (var frame in chain.Frames)
            {
                if (frame is not CorDebugILFrame il) continue;
                try
                {
                    var function = il.Function;
                    var module = ModuleFor(function.Module);
                    frames.Add(new FrameInfo(il, module, function.Token, il.IP.pnOffset, frames.Count == 0));
                }
                catch (Exception ex) when (ex is COMException or DebugException) { }
            }
        }
        _frameCache[thread.Id] = (_generation, frames);
        return frames;
    }

    public FrameInfo? FrameAt(int threadId, int index)
    {
        if (_process is null) return null;
        try
        {
            var frames = WalkFrames(_process.GetThread(threadId));
            return index >= 0 && index < frames.Count ? frames[index] : null;
        }
        catch (Exception ex) when (ex is COMException or DebugException) { return null; }
    }

    public CorDebugThread? Thread(int threadId)
    {
        if (_process is null) return null;
        try { return _process.GetThread(threadId); }
        catch (Exception ex) when (ex is COMException or DebugException) { return null; }
    }

    public ModuleEntry? ModuleFor(CorDebugModule module)
    {
        ulong address;
        try { address = module.BaseAddress.Value; }
        catch (Exception ex) when (ex is COMException or DebugException) { return null; }
        if (_modules.TryGetValue(address, out var entry)) return entry;
        return RegisterModule(module, loadSymbols: false);
    }

    public IReadOnlyCollection<ModuleEntry> Modules
    {
        get { lock (Sync) return _modules.Values.ToList(); }
    }

    // ---------------------------------------------------------------- コールバック

    private CorDebugManagedCallback CreateCallback()
    {
        var callback = new CorDebugManagedCallback();
        callback.OnCreateProcess += (_, e) => Guard(e, () => OnCreateProcess(e));
        callback.OnCreateAppDomain += (_, e) => Guard(e, () => e.AppDomain.Attach());
        callback.OnLoadModule += (_, e) => Guard(e, () => OnLoadModule(e.Module));
        callback.OnUnloadModule += (_, e) => Guard(e, () => OnUnloadModule(e.Module));
        callback.OnCreateThread += (_, e) => Guard(e, () => _dap.SendEvent("thread", new { reason = "started", threadId = e.Thread.Id }));
        callback.OnExitThread += (_, e) => Guard(e, () => _dap.SendEvent("thread", new { reason = "exited", threadId = e.Thread.Id }));
        callback.OnBreakpoint += (_, e) => Guard(e, () => OnBreakpoint(e));
        callback.OnStepComplete += (_, e) => Guard(e, () => OnStepComplete(e));
        callback.OnBreak += (_, e) => Guard(e, () => OnUserBreak(e));
        callback.OnException2 += (_, e) => Guard(e, () => OnException(e));
        callback.OnEvalComplete += (_, e) => Guard(e, () => OnEvalFinished(e, e.Eval, threw: false));
        callback.OnEvalException += (_, e) => Guard(e, () => OnEvalFinished(e, e.Eval, threw: true));
        callback.OnExitProcess += (_, e) => Guard(e, () => OnExitProcess(e));
        callback.OnLogMessage += (_, e) => Guard(e, () => _dap.Output(e.Message ?? "", "console"));
        callback.OnDebuggerError += (_, e) => Guard(e, () => Log($"デバッガーのエラー: 0x{(int)e.ErrorHR:X8}"));
        callback.OnAnyEvent += (_, e) =>
        {
            lock (Sync)
            {
                if (!e.Continue) return;
                try { e.Controller.Continue(false); }
                catch (Exception ex) when (ex is COMException or DebugException) { }
            }
        };
        return callback;
    }

    private void Guard(CorDebugManagedCallbackEventArgs e, Action action)
    {
        lock (Sync)
        {
            try { action(); }
            catch (Exception ex) when (ex is COMException or DebugException or InvalidOperationException or IOException)
            {
                Log($"コールバック処理でエラー（{e.Kind}）: {ex.Message}");
            }
        }
    }

    private void OnCreateProcess(CreateProcessCorDebugManagedCallbackEventArgs e)
    {
        _process ??= e.Process;
        if (_launching && !_configured)
        {
            // 構成（ブレークポイント）が揃うまで走らせない。configurationDone で Continue する。
            e.Continue = false;
            _stopped = true;
        }
    }

    private void OnLoadModule(CorDebugModule module)
    {
        var entry = RegisterModule(module, loadSymbols: true);
        if (entry is null) return;
        foreach (var breakpoint in _sourceBreakpoints.Values.SelectMany(l => l)) Bind(breakpoint, entry);
        if (_stopAtEntry && _entryBreakpoint is null && _programPath is not null &&
            string.Equals(entry.Path, _programPath, StringComparison.OrdinalIgnoreCase))
            SetEntryBreakpoint(entry);
        _dap.SendEvent("module", new
        {
            reason = "new",
            module = new { id = entry.Id, name = entry.Name, path = entry.Path, symbolStatus = SymbolStatus(entry) },
        });
    }

    private void OnUnloadModule(CorDebugModule module)
    {
        ulong address;
        try { address = module.BaseAddress.Value; }
        catch (Exception ex) when (ex is COMException or DebugException) { return; }
        if (!_modules.TryGetValue(address, out var entry)) return;
        _modules.Remove(address);
        foreach (var breakpoint in _sourceBreakpoints.Values.SelectMany(l => l))
            breakpoint.Bound.RemoveAll(b => b.Module == entry);
    }

    private ModuleEntry? RegisterModule(CorDebugModule module, bool loadSymbols)
    {
        string path;
        try
        {
            if (module.IsDynamic || module.IsInMemory) return null;
            path = module.Name;
        }
        catch (Exception ex) when (ex is COMException or DebugException) { return null; }
        var entry = new ModuleEntry(module, path) { Id = ++_nextModuleId };
        _modules[entry.BaseAddress] = entry;
        entry.Metadata = ModuleMetadata.For(path);
        if (loadSymbols && !IsFrameworkPath(path))
        {
            entry.Symbols = SymbolLoader.Load(path, module, Log);
            // シンボルのあるモジュールだけを「マイコード」にする（JMC ステップはそれ以外に入らない）。
            try { module.SetJMCStatus(entry.Symbols is not null, 0, null); }
            catch (Exception ex) when (ex is COMException or DebugException) { }
            if (entry.Symbols is not null)
            {
                // 最適化を切ってローカル変数と行の対応を保つ（Release ビルドでも変数が見えるように）。
                try { module.EnableJITDebugging(true, false); }
                catch (Exception ex) when (ex is COMException or DebugException) { }
            }
        }
        return entry;
    }

    private static bool IsFrameworkPath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static string SymbolStatus(ModuleEntry entry)
        => entry.Symbols is { } symbols ? $"シンボル読み込み済み（{symbols.Kind}）" : "シンボルなし";

    private void SetEntryBreakpoint(ModuleEntry module)
    {
        // PDB のユーザーエントリポイントを優先（async Main の実体は <Main> ではなく Main、本体は MoveNext）。
        var token = module.Symbols?.UserEntryPoint is { } user && user != 0 ? user : module.Metadata?.EntryPointToken ?? 0;
        var method = token == 0 ? null : module.Symbols?.Method(token);
        // async Main の本当の入口は合成された <Main>（中で Main を呼ぶだけ）。書いた Main へ寄せる。
        if ((method is null || method.Points.All(p => p.IsHidden)) && module.Metadata?.Method(token) is { Name: "<Main>" } synthesized &&
            module.Metadata.Type(synthesized.DeclaringTypeToken)?.Methods.FirstOrDefault(m => m.Name == "Main") is { } userMain)
        {
            token = userMain.Token;
            method = module.Symbols?.Method(token);
        }
        if ((method is null || method.Points.All(p => p.IsHidden)) &&
            module.Symbols?.KickoffToMoveNext.TryGetValue(token, out var moveNext) == true)
        {
            token = moveNext;
            method = module.Symbols.Method(token);
        }
        var point = method?.Points.FirstOrDefault(p => !p.IsHidden);
        if (method is null || point is null) return;
        try
        {
            var bp = module.Module.GetFunctionFromToken(token).ILCode.CreateBreakpoint(point.Offset);
            bp.Activate(true);
            _entryBreakpoint = new SourceBreakpoint { IsTemporaryEntry = true, Path = point.DocumentKey, Line = point.StartLine };
            _entryBreakpoint.Bound.Add(new BoundBreakpoint(module, token, point.Offset, bp));
        }
        catch (Exception ex) when (ex is COMException or DebugException) { }
    }

    private void OnBreakpoint(BreakpointCorDebugManagedCallbackEventArgs e)
    {
        if (_evalInProgress) return;
        var breakpoint = FindBreakpoint(e.Breakpoint);
        if (breakpoint is null) return;
        var threadId = e.Thread.Id;

        if (breakpoint.IsTemporaryEntry)
        {
            breakpoint.Bound.ForEach(b => b.Breakpoint.TryActivate(false));
            _entryBreakpoint = null;
            StopHere(e, "entry", threadId);
            return;
        }

        breakpoint.HitCount++;
        if (!HitConditionMet(breakpoint)) return;

        if (string.IsNullOrWhiteSpace(breakpoint.Condition) && string.IsNullOrWhiteSpace(breakpoint.LogMessage))
        {
            StopHere(e, "breakpoint", threadId);
            return;
        }

        // 条件式・ログメッセージの評価は関数評価（対象を走らせる）を伴いうるので、コールバックスレッドでは
        // やらない（評価の完了もこのスレッドに届くので、ここで待つと自分を待つことになる）。止めたまま
        // ワーカーへ渡し、結果に応じて止まる／黙って続行する。
        e.Continue = false;
        _stopped = true;
        _generation++;
        _work.Add(() => CheckConditionalBreakpoint(breakpoint, threadId));
    }

    private void CheckConditionalBreakpoint(SourceBreakpoint breakpoint, int threadId)
    {
        lock (Sync)
        {
            if (!_stopped) return;
            if (!string.IsNullOrWhiteSpace(breakpoint.Condition))
            {
                var result = EvaluateCondition(breakpoint.Condition!, threadId);
                if (result is null)
                {
                    // 評価できない条件は「止まる」側に倒す（VS と同じ。黙って素通りすると条件の誤りに気付けない）。
                    Log($"ブレークポイントの条件を評価できませんでした: {breakpoint.Condition}");
                }
                else if (result == false)
                {
                    Resume(sendContinued: false);
                    return;
                }
            }
            if (!string.IsNullOrWhiteSpace(breakpoint.LogMessage))
            {
                _dap.Output(InterpolateLogMessage(breakpoint.LogMessage!, threadId), "console");
                Resume(sendContinued: false);
                return;
            }
            _stoppedThreadId = threadId;
            ReportStopped("breakpoint", threadId);
        }
    }

    private static bool HitConditionMet(SourceBreakpoint breakpoint)
    {
        var text = breakpoint.HitCondition?.Trim();
        if (text is null || text.Length == 0) return true;
        var op = new string(text.TakeWhile(c => c is '<' or '>' or '=' or '%' or '!').ToArray());
        if (!int.TryParse(text.Substring(op.Length).Trim(), out var n)) return true;
        var hits = breakpoint.HitCount;
        return op switch
        {
            ">" => hits > n,
            ">=" => hits >= n,
            "<" => hits < n,
            "<=" => hits <= n,
            "%" => n > 0 && hits % n == 0,
            "!=" => hits != n,
            "=" or "==" => hits == n,
            // 数字だけなら「その回数目以降」（netcoredbg と同じ）。
            _ => hits >= n,
        };
    }

    private void OnStepComplete(StepCompleteCorDebugManagedCallbackEventArgs e)
    {
        if (_evalInProgress) return;
        _stepper = null;
        var thread = e.Thread;
        _generation++;
        var top = WalkFrames(thread).FirstOrDefault();
        var method = top?.Module?.Symbols?.Method(top.MethodToken);
        var point = method?.At(top!.ILOffset);

        // 行に対応しない場所（隠し区間・シンボルの無いコード）で止まったら、もう一歩進める。
        var needsMore = top is null || method is null || point is null || point.IsHidden;
        if (needsMore && _restepCount++ < 50)
        {
            var kind = method is null ? StepKind.Out : _stepKind == StepKind.Out ? StepKind.Over : _stepKind;
            if (BeginStep(thread, kind))
            {
                _generation++;
                return; // e.Continue のまま走らせる
            }
        }
        StopHere(e, "step", thread.Id);
    }

    private void OnUserBreak(BreakCorDebugManagedCallbackEventArgs e)
    {
        if (_evalInProgress) return;
        StopHere(e, "pause", e.Thread.Id);
    }

    private void OnException(Exception2CorDebugManagedCallbackEventArgs e)
    {
        if (_evalInProgress) return;
        var stop = e.EventType switch
        {
            CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE => _breakOnAll,
            CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED => true,
            CorDebugExceptionCallbackType.DEBUG_EXCEPTION_CATCH_HANDLER_FOUND => _breakOnUserUnhandled && !_breakOnAll &&
                IsUserUnhandled(e),
            _ => false,
        };
        if (!stop) return;
        _stopDescription = e.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED
            ? "ハンドルされていない例外"
            : "例外がスローされました";
        StopHere(e, "exception", e.Thread.Id);
    }

    /// <summary>例外がユーザーコードで投げられ、捕まえる場所がユーザーコードではない（フレームワークの中）か。</summary>
    private bool IsUserUnhandled(Exception2CorDebugManagedCallbackEventArgs e)
    {
        try
        {
            if (e.Frame is not CorDebugILFrame handler) return false;
            var handlerModule = ModuleFor(handler.Function.Module);
            if (handlerModule?.Symbols is not null) return false;
            var thrower = WalkFrames(e.Thread).FirstOrDefault(f => f.Module?.Symbols is not null);
            return thrower is not null;
        }
        catch (Exception ex) when (ex is COMException or DebugException) { return false; }
    }

    private void OnExitProcess(ExitProcessCorDebugManagedCallbackEventArgs e)
    {
        e.Continue = false;
        _stopped = false;
        // このコールバックの時点では OS のプロセスはまだ終わりきっておらず、終了コードも取れない。
        // コールバックを返してから（ワーカーで）終了を待ち、終了コード付きで知らせる。
        _work.Add(() =>
        {
            int? exitCode = null;
            try
            {
                if (_osProcess is { } os && os.WaitForExit(5000)) exitCode = os.ExitCode;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _dap.SendEvent("exited", new { exitCode = exitCode ?? 0 });
            _dap.SendEvent("terminated", new { });
            lock (Sync)
            {
                _exited.Set();
                Monitor.PulseAll(Sync);
            }
        });
    }

    private string? _stopDescription;

    private void StopHere(CorDebugManagedCallbackEventArgs e, string reason, int threadId)
    {
        e.Continue = false;
        _stopped = true;
        _generation++;
        ReportStopped(reason, threadId);
    }

    private void ReportStopped(string reason, int threadId)
    {
        _stepper?.TryDeactivate();
        _stepper = null;
        _stoppedThreadId = threadId;
        var description = _stopDescription;
        _stopDescription = null;
        _dap.SendEvent("stopped", new { reason, threadId, allThreadsStopped = true, description });
    }

    // ---------------------------------------------------------------- 後始末

    /// <summary>止まっていた間だけ有効なもの（フレーム id・変数参照・ハンドル）を捨てる。</summary>
    private void ClearStopState(bool keepHandles = false)
    {
        _frameCache.Clear();
        ClearReferences();
        if (!keepHandles) DisposeHandles();
    }

    internal CorDebugHandleValue? KeepAlive(CorDebugValue value)
    {
        try
        {
            if (value.Raw is not ICorDebugHeapValue2 heap) return null;
            if (heap.CreateHandle(CorDebugHandleType.HANDLE_STRONG, out var raw) != HRESULT.S_OK || raw is null) return null;
            var handle = new CorDebugHandleValue(raw);
            _handles.Add(handle);
            return handle;
        }
        catch (Exception ex) when (ex is COMException or DebugException or InvalidCastException) { return null; }
    }

    private void DisposeHandles()
    {
        foreach (var handle in _handles)
        {
            try { handle.Dispose(); }
            catch (Exception ex) when (ex is COMException or DebugException) { }
        }
        _handles.Clear();
    }

    // ---------------------------------------------------------------- 出力の中継

    /// <summary>対象の標準出力／標準エラーを読み、<b>1 行ずつ</b> output イベントにする（Loomo はイベント 1 つを
    /// 1 行として表示する。Console.WriteLine は本文と改行を別々に書くので、そのまま送ると空行が挟まる）。
    /// 改行の来ない書きかけ（入力の促しなど）は、少し待っても続きが来なければそのまま送る。</summary>
    private void PumpOutput(IntPtr readHandle, string category)
    {
        var encoding = ConsoleEncoding();
        var pending = new StringBuilder();
        var gate = new object();
        Timer? flushTimer = null;

        void Flush(bool complete)
        {
            lock (gate)
            {
                while (true)
                {
                    var text = pending.ToString();
                    var newline = text.IndexOf('\n');
                    if (newline < 0)
                    {
                        if (complete && text.Length > 0)
                        {
                            _dap.SendEvent("output", new { category, output = text.TrimEnd('\r') + "\n" });
                            pending.Clear();
                        }
                        return;
                    }
                    _dap.SendEvent("output", new { category, output = text.Substring(0, newline).TrimEnd('\r') + "\n" });
                    pending.Remove(0, newline + 1);
                }
            }
        }

        flushTimer = new Timer(_ => Flush(complete: true), null, Timeout.Infinite, Timeout.Infinite);
        var thread = new Thread(() =>
        {
            using var stream = new FileStream(new SafeFileHandle(readHandle, true), FileAccess.Read, 4096, false);
            using var reader = new StreamReader(stream, encoding);
            var buffer = new char[4096];
            while (true)
            {
                int read;
                try { read = reader.Read(buffer, 0, buffer.Length); }
                catch (IOException) { break; }
                if (read <= 0) break;
                lock (gate) pending.Append(buffer, 0, read);
                Flush(complete: false);
                flushTimer.Change(200, Timeout.Infinite);
            }
            flushTimer.Dispose();
            Flush(complete: true);
        }) { IsBackground = true, Name = "NetFxDebug " + category };
        thread.Start();
    }

    /// <summary>対象のコンソール出力の文字コード。.NET Framework のコンソールは OEM コードページで書く。</summary>
    private static Encoding ConsoleEncoding()
    {
        try { return Encoding.GetEncoding((int)GetOEMCP()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Default; }
    }

    private static IntPtr CreateOutputPipe(out IntPtr writeHandle)
    {
        var attributes = new SECURITY_ATTRIBUTES_NATIVE
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES_NATIVE>(),
            bInheritHandle = true,
        };
        if (!CreatePipe(out var read, out writeHandle, ref attributes, 0))
            throw new IOException("出力用のパイプを作れませんでした。");
        // 読み取り側は対象に継承させない（させると対象が生きている限りパイプが閉じない）。
        SetHandleInformation(read, HandleFlagInherit, 0);
        return read;
    }

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value ?? "";
        foreach (var pair in overrides) variables[pair.Key] = pair.Value;
        var builder = new StringBuilder();
        foreach (var pair in variables) builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    /// <summary>Windows のコマンドライン引数規則（CommandLineToArgvW）に沿って 1 引数を引用する。</summary>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { builder.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            builder.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    /// <summary>アダプタ自身の標準入出力を子へ継承させない。させると、デバッグ対象が Loomo とのパイプを握り、
    /// アダプタが終わってもパイプが閉じない（Loomo 側が終了を検知できない）。</summary>
    public static void MakeStandardHandlesNonInheritable()
    {
        foreach (var id in new[] { -10, -11, -12 })
        {
            var handle = GetStdHandle(id);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) SetHandleInformation(handle, HandleFlagInherit, 0);
        }
    }

    private const uint HandleFlagInherit = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES_NATIVE
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SECURITY_ATTRIBUTES_NATIVE attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int id);

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
