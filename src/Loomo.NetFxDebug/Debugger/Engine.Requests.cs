using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;

namespace sk0ya.Loomo.NetFxDebug.Debugger;

internal sealed partial class Engine
{
    /// <summary>ウォッチ・イミディエイトの評価。<paramref name="frameId"/> が無ければ停止スレッドの先頭フレーム。</summary>
    public EvaluationResult Evaluate(string expression, int? frameId)
    {
        lock (Sync)
        {
            WaitIdle();
            if (!_stopped || _process is null) throw new EvaluationException("実行中は評価できません（一時停止してください）。");
            var (threadId, frameIndex) = frameId is { } id && FrameRef(id) is { } frame ? frame : (_stoppedThreadId, 0);
            return new Evaluator(this, threadId, frameIndex).Evaluate(expression);
        }
    }

    /// <summary>条件付きブレークポイントの条件（ロック内・停止中に呼ぶ）。評価できなければ null。</summary>
    private bool? EvaluateCondition(string condition, int threadId)
        => new Evaluator(this, threadId, 0).EvaluateBoolean(condition);

    /// <summary>ログポイントの文言。<c>{式}</c> を評価結果で置き換える。</summary>
    private string InterpolateLogMessage(string template, int threadId)
    {
        var evaluator = new Evaluator(this, threadId, 0);
        var builder = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '{') { builder.Append(c); continue; }
            var close = template.IndexOf('}', i + 1);
            if (close < 0) { builder.Append(template.Substring(i)); break; }
            var expression = template.Substring(i + 1, close - i - 1);
            try { builder.Append(evaluator.Evaluate(expression).Display); }
            catch (EvaluationException ex) { builder.Append("<").Append(ex.Message).Append(">"); }
            i = close;
        }
        return builder.ToString();
    }

    /// <summary>停止の原因になった例外（exceptionInfo 用）。</summary>
    public (string Id, string Description, string Break)? ExceptionInfo(int threadId)
    {
        lock (Sync)
        {
            WaitIdle();
            if (!_stopped || Thread(threadId) is not { } thread) return null;
            try
            {
                var current = thread.CurrentException;
                if (current is null || Inspector.IsNullReference(current)) return null;
                if (Inspector.Dereference(current) is not CorDebugObjectValue obj || Inspector.ExactType(obj) is not { } type)
                    return null;
                var name = Inspector.TypeName(this, type);
                var message = Inspector.FieldValue(this, obj, type, "_message");
                var text = message is not null && Inspector.Dereference(message) is CorDebugStringValue s
                    ? Inspector.ReadString(s)
                    : "";
                return (name, text, "always");
            }
            catch (Exception ex) when (ex is COMException or DebugException) { return null; }
        }
    }

    /// <summary>mscorlib の型（System.String 等）の CorDebugType。無ければ null。</summary>
    public CorDebugType? WellKnownType(string fullName)
    {
        var mscorlib = _modules.Values.FirstOrDefault(m =>
            string.Equals(m.Name, "mscorlib.dll", StringComparison.OrdinalIgnoreCase));
        if (mscorlib?.Metadata?.FindType(fullName) is not { } token) return null;
        try { return mscorlib.Module.GetClassFromToken(token).GetParameterizedType(CorElementType.Class, 0, null); }
        catch (Exception ex) when (ex is COMException or DebugException) { return null; }
    }

    /// <summary>次のステートメントの候補（gotoTargets）：行に対応する区間が今のメソッドにあれば 1 件。</summary>
    public bool HasGotoTarget(int threadId, int line)
    {
        lock (Sync)
        {
            WaitIdle();
            if (!_stopped || Thread(threadId) is not { } thread) return false;
            var frame = WalkFrames(thread).FirstOrDefault();
            return frame?.Module?.Symbols?.PointOnLine(frame.MethodToken, line) is not null;
        }
    }
}
