using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ClrDebug;

namespace sk0ya.Loomo.NetFxDebug.Debugger;

/// <summary>関数評価 1 回の結果。<see cref="Threw"/> のとき <see cref="Value"/> は投げられた例外オブジェクト。</summary>
internal sealed class EvalOutcome
{
    public EvalOutcome(CorDebugValue? value, bool threw, string? error)
    {
        Value = value;
        Threw = threw;
        Error = error;
    }

    public CorDebugValue? Value { get; }
    public bool Threw { get; }
    public string? Error { get; }
}

internal sealed partial class Engine
{
    private bool _evalInProgress;
    private bool _evalDone;
    private bool _evalThrew;
    private int _evalThreadId;
    private int _evalTimeouts;

    /// <summary>関数評価をしてよい状態か。止まっていて、評価のタイムアウトが続いていないこと
    /// （応答しない評価を何度も待たせない）。</summary>
    public bool CanEvaluate => _stopped && _process is not null && !_exited.IsSet && _evalTimeouts < 2;

    /// <summary>評価中は他の要求に ICorDebug を触らせない（評価のあいだ対象は走っている）。ロック内で呼ぶ。</summary>
    public void WaitIdle()
    {
        while (_evalInProgress) Monitor.Wait(Sync);
    }

    /// <summary>
    /// 関数評価を 1 回行う（ロック内・停止中に呼ぶ）。<paramref name="start"/> で評価を仕掛け、評価スレッド以外を
    /// 止めたまま対象を走らせ、EvalComplete／EvalException を待つ。時間切れなら Abort する。
    /// 走らせるので、戻った後はフレームと値を辿り直すこと（<see cref="Generation"/> が進む）。
    /// </summary>
    public EvalOutcome RunEval(CorDebugThread thread, Action<CorDebugEval> start, int timeoutMs = 3000)
    {
        if (!CanEvaluate || _evalInProgress) return new EvalOutcome(null, false, "評価できる状態ではありません。");
        var process = _process!;
        CorDebugEval eval;
        try
        {
            eval = thread.CreateEval();
            start(eval);
        }
        catch (Exception ex) when (ex is COMException or DebugException)
        {
            return new EvalOutcome(null, false, EvalErrorText(ex));
        }

        _evalInProgress = true;
        _evalDone = false;
        _evalThrew = false;
        _evalThreadId = thread.Id;
        try
        {
            try { process.SetAllThreadsDebugState(CorDebugThreadState.THREAD_SUSPEND, thread.Raw); }
            catch (Exception ex) when (ex is COMException or DebugException) { }

            _stopped = false;
            _generation++;
            _frameCache.Clear();
            process.Continue(false);

            var deadline = Environment.TickCount + timeoutMs;
            while (!_evalDone && !_exited.IsSet)
            {
                var remaining = deadline - Environment.TickCount;
                if (remaining <= 0 || !Monitor.Wait(Sync, remaining)) break;
            }

            if (!_evalDone && !_exited.IsSet)
            {
                _evalTimeouts++;
                try { eval.Abort(); } catch (Exception ex) when (ex is COMException or DebugException) { }
                var abortDeadline = Environment.TickCount + 2000;
                while (!_evalDone && !_exited.IsSet)
                {
                    var remaining = abortDeadline - Environment.TickCount;
                    if (remaining <= 0 || !Monitor.Wait(Sync, remaining)) break;
                }
                if (!_evalDone && !_exited.IsSet)
                {
                    // 中断もできなかった。ともかく同期状態へ戻す（以後の評価はしない）。
                    try { process.Stop(0); } catch (Exception ex) when (ex is COMException or DebugException) { }
                    _stopped = true;
                    _generation++;
                    _evalTimeouts = int.MaxValue / 2;
                }
                return new EvalOutcome(null, false, "評価がタイムアウトしました。");
            }
            if (_exited.IsSet) return new EvalOutcome(null, false, "プロセスが終了しました。");

            if (eval.TryGetResult(out var result) != HRESULT.S_OK) result = null;
            return new EvalOutcome(result, _evalThrew, null);
        }
        catch (Exception ex) when (ex is COMException or DebugException)
        {
            return new EvalOutcome(null, false, EvalErrorText(ex));
        }
        finally
        {
            try { process.SetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN, null); }
            catch (Exception ex) when (ex is COMException or DebugException) { }
            _evalInProgress = false;
            Monitor.PulseAll(Sync);
        }
    }

    private void OnEvalFinished(CorDebugManagedCallbackEventArgs e, CorDebugEval eval, bool threw)
    {
        if (!_evalInProgress) return;
        e.Continue = false;
        _stopped = true;
        _generation++;
        _evalDone = true;
        _evalThrew = threw;
        Monitor.PulseAll(Sync);
    }

    private static string EvalErrorText(Exception ex)
    {
        if (ex is DebugException debug)
        {
            return debug.HResult switch
            {
                HRESULT.CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT =>
                    "この位置では関数を評価できません（中断した場所が安全点ではありません。ステップ実行すると評価できます）。",
                HRESULT.CORDBG_E_FUNC_EVAL_BAD_START_POINT => "この位置では関数を評価できません。",
                _ => "評価に失敗しました: " + debug.HResult,
            };
        }
        return "評価に失敗しました: " + ex.Message;
    }

    /// <summary>メソッドを呼ぶ（インスタンスメソッドなら <paramref name="args"/> の先頭が this）。</summary>
    public EvalOutcome Call(CorDebugThread thread, ModuleEntry module, int methodToken, CorDebugType[] typeArguments,
        CorDebugValue[] args, int timeoutMs = 3000)
    {
        CorDebugFunction function;
        try { function = module.Module.GetFunctionFromToken(methodToken); }
        catch (Exception ex) when (ex is COMException or DebugException) { return new EvalOutcome(null, false, EvalErrorText(ex)); }
        return RunEval(thread, eval => eval.CallParameterizedFunction(function.Raw, typeArguments.Length,
            typeArguments.Select(t => t.Raw).ToArray(), args.Length, args.Select(a => a.Raw).ToArray()), timeoutMs);
    }

    /// <summary>対象の中に文字列を作る（setVariable や引数に渡すため）。</summary>
    public EvalOutcome NewString(CorDebugThread thread, string text)
        => RunEval(thread, eval => eval.NewString(text));

    /// <summary>対象の中にプリミティブ値を作る（引数に渡すため。評価で走らせる必要はない）。</summary>
    public CorDebugValue? NewPrimitive(CorDebugThread thread, CorElementType type, object value)
    {
        try
        {
            var eval = thread.CreateEval();
            var created = eval.CreateValue(type, null);
            if (created is not CorDebugGenericValue generic) return null;
            var text = value switch
            {
                bool b => b ? "true" : "false",
                char c => ((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };
            return Inspector.WritePrimitive(generic, type, text, out _) ? generic : null;
        }
        catch (Exception ex) when (ex is COMException or DebugException) { return null; }
    }
}
