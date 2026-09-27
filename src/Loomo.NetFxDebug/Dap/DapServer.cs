using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClrDebug;
using sk0ya.Loomo.NetFxDebug.Debugger;

namespace sk0ya.Loomo.NetFxDebug.Dap;

/// <summary>
/// DAP の要求を受けて <see cref="Engine"/> へ振り分ける。要求は受信スレッドで 1 つずつ処理する
/// （Loomo の DAP クライアントは応答を待って次を送るので、並べる必要が無い）。
/// </summary>
internal sealed class DapServer
{
    private readonly DapChannel _channel;
    private readonly Engine _engine;
    private bool _disconnected;
    private int _gotoLine;
    private int _gotoThread;

    public DapServer(DapChannel channel)
    {
        _channel = channel;
        _engine = new Engine(channel);
    }

    public int Run()
    {
        while (!_disconnected)
        {
            JsonDocument? message;
            try { message = _channel.Read(); }
            catch (Exception ex) when (ex is JsonException or FormatException or System.IO.IOException) { break; }
            if (message is null) break;
            using (message)
            {
                var root = message.RootElement;
                if (Str(root, "type") != "request") continue;
                var seq = Int(root, "seq") ?? 0;
                var command = Str(root, "command") ?? "";
                var args = root.TryGetProperty("arguments", out var a) ? a : default;
                try
                {
                    var body = Handle(command, args, out var deferred);
                    _channel.SendResponse(seq, command, true, body);
                    deferred?.Invoke();
                }
                catch (Exception ex) when (ex is EvaluationException or InvalidOperationException or COMException
                                               or DebugException or ArgumentException or System.IO.IOException
                                               or NotSupportedException or UnauthorizedAccessException)
                {
                    _channel.SendResponse(seq, command, false, new { error = new { id = 1, format = ex.Message } }, ex.Message);
                }
            }
        }
        // disconnect が来ないまま Loomo 側が閉じた（落ちた）。起動した対象は道連れにするが、アタッチした対象は
        // 利用者のプロセスなので切り離すだけにする（通常の disconnect と同じ使い分け）。
        if (!_disconnected) _engine.Disconnect(terminateDebuggee: !_engine.IsAttached);
        _engine.Shutdown();
        return 0;
    }

    /// <summary>要求 1 つを処理して応答の body を返す。<paramref name="afterResponse"/> は応答を送った後に
    /// 行う処理（initialized イベントは launch の応答より後に届くのが DAP の順序）。</summary>
    private object? Handle(string command, JsonElement args, out Action? afterResponse)
    {
        afterResponse = null;
        switch (command)
        {
            case "initialize":
                return new
                {
                    supportsConfigurationDoneRequest = true,
                    supportsSetVariable = true,
                    supportsGotoTargetsRequest = true,
                    supportsConditionalBreakpoints = true,
                    supportsHitConditionalBreakpoints = true,
                    supportsLogPoints = true,
                    supportsExceptionInfoRequest = true,
                    supportsTerminateRequest = true,
                    supportsModulesRequest = true,
                    supportsEvaluateForHovers = true,
                    exceptionBreakpointFilters = new object[]
                    {
                        new { filter = "all", label = "スローされたすべての例外", @default = false },
                        new { filter = "user-unhandled", label = "ユーザーコードで処理されない例外", @default = true },
                    },
                };

            case "launch":
            {
                var program = Str(args, "program") ?? throw new ArgumentException("program が指定されていません。");
                if (!System.IO.File.Exists(program)) throw new ArgumentException($"実行対象が見つかりません: {program}");
                var arguments = args.TryGetProperty("args", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                    : Array.Empty<string>();
                Dictionary<string, string>? env = null;
                if (args.TryGetProperty("env", out var envElement) && envElement.ValueKind == JsonValueKind.Object)
                    env = envElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
                _engine.Launch(program, arguments, Str(args, "cwd"), env,
                    Bool(args, "stopAtEntry") ?? false, Bool(args, "justMyCode") ?? true);
                afterResponse = () => _channel.SendEvent("initialized");
                return null;
            }

            case "attach":
            {
                var processId = Int(args, "processId") ?? throw new ArgumentException("processId が指定されていません。");
                _engine.Attach(processId, Bool(args, "justMyCode") ?? true);
                afterResponse = () => _channel.SendEvent("initialized");
                return null;
            }

            case "setBreakpoints":
            {
                var path = args.TryGetProperty("source", out var source) ? Str(source, "path") : null;
                if (path is null) return new { breakpoints = Array.Empty<object>() };
                var requested = new List<SourceBreakpoint>();
                if (args.TryGetProperty("breakpoints", out var bps) && bps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bp in bps.EnumerateArray())
                    {
                        requested.Add(new SourceBreakpoint
                        {
                            Line = Int(bp, "line") ?? 0,
                            Condition = Str(bp, "condition"),
                            HitCondition = Str(bp, "hitCondition"),
                            LogMessage = Str(bp, "logMessage"),
                        });
                    }
                }
                var result = _engine.SetBreakpoints(path, requested);
                return new
                {
                    breakpoints = result.Select(b => new
                    {
                        id = b.Id,
                        verified = b.Bound.Count > 0,
                        line = b.ActualLine ?? b.Line,
                        message = b.Bound.Count > 0 ? null : "まだ読み込まれていないコード、またはシンボルが見つかりません。",
                    }).ToArray(),
                };
            }

            case "setExceptionBreakpoints":
            {
                var filters = args.TryGetProperty("filters", out var f) && f.ValueKind == JsonValueKind.Array
                    ? f.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : new List<string>();
                _engine.SetExceptionFilters(filters);
                return null;
            }

            case "configurationDone":
                _engine.ConfigurationDone();
                return null;

            case "threads":
                return new { threads = _engine.Threads().Select(t => new { id = t.Id, name = t.Name }).ToArray() };

            case "stackTrace":
            {
                var threadId = Int(args, "threadId") ?? _engine.StoppedThreadId;
                var frames = _engine.StackTrace(threadId, Int(args, "startFrame") ?? 0, Int(args, "levels") ?? 0);
                return new
                {
                    stackFrames = frames.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        source = f.Path is null ? null : new { name = System.IO.Path.GetFileName(f.Path), path = f.Path },
                        line = f.Line,
                        column = Math.Max(1, f.Column),
                        presentationHint = f.Path is null ? "subtle" : null,
                    }).ToArray(),
                    totalFrames = frames.Count,
                };
            }

            case "scopes":
            {
                var frameId = Int(args, "frameId") ?? 0;
                var reference = _engine.ScopeReference(frameId);
                return new
                {
                    scopes = reference == 0
                        ? Array.Empty<object>()
                        : new object[] { new { name = "ローカル", variablesReference = reference, expensive = false } },
                };
            }

            case "variables":
            {
                var items = _engine.Variables(Int(args, "variablesReference") ?? 0);
                return new { variables = items.Select(ToDap).ToArray() };
            }

            case "evaluate":
            {
                var expression = Str(args, "expression") ?? "";
                var result = _engine.Evaluate(expression, Int(args, "frameId"));
                return new { result = result.Display, type = result.Type, variablesReference = result.Reference };
            }

            case "setVariable":
            {
                var value = _engine.SetVariable(Int(args, "variablesReference") ?? 0, Str(args, "name") ?? "",
                    Str(args, "value") ?? "", out var error);
                if (value is null) throw new InvalidOperationException(error ?? "書き換えに失敗しました。");
                return new { value };
            }

            case "continue":
                _engine.Continue();
                return new { allThreadsContinued = true };

            case "next":
                _engine.Step(Int(args, "threadId") ?? _engine.StoppedThreadId, StepKind.Over);
                return null;

            case "stepIn":
                _engine.Step(Int(args, "threadId") ?? _engine.StoppedThreadId, StepKind.In);
                return null;

            case "stepOut":
                _engine.Step(Int(args, "threadId") ?? _engine.StoppedThreadId, StepKind.Out);
                return null;

            case "pause":
                _engine.Pause();
                return null;

            case "exceptionInfo":
            {
                var info = _engine.ExceptionInfo(Int(args, "threadId") ?? _engine.StoppedThreadId);
                if (info is null) throw new InvalidOperationException("例外の情報がありません。");
                return new
                {
                    exceptionId = info.Value.Id,
                    description = info.Value.Description,
                    breakMode = info.Value.Break,
                    details = new { message = info.Value.Description, typeName = info.Value.Id },
                };
            }

            case "gotoTargets":
            {
                var line = Int(args, "line") ?? 0;
                _gotoLine = line;
                _gotoThread = _engine.StoppedThreadId;
                return new
                {
                    targets = _engine.HasGotoTarget(_gotoThread, line)
                        ? new object[] { new { id = 1, label = $"{line} 行目", line } }
                        : Array.Empty<object>(),
                };
            }

            case "goto":
            {
                var threadId = Int(args, "threadId") ?? _gotoThread;
                if (!_engine.SetNextStatement(threadId, _gotoLine, out var error))
                    throw new InvalidOperationException(error ?? "移動できませんでした。");
                afterResponse = () => _channel.SendEvent("stopped", new { reason = "goto", threadId, allThreadsStopped = true });
                return null;
            }

            case "modules":
                return new
                {
                    modules = _engine.Modules.Select(m => new
                    {
                        id = m.Id,
                        name = m.Name,
                        path = m.Path,
                        symbolStatus = Engine.SymbolStatus(m),
                    }).ToArray(),
                };

            case "terminate":
                _engine.Disconnect(terminateDebuggee: true);
                return null;

            case "disconnect":
                _engine.Disconnect(Bool(args, "terminateDebuggee") ?? true);
                _disconnected = true;
                return null;

            default:
                throw new NotSupportedException($"未対応の要求です: {command}");
        }
    }

    private static object ToDap(VariableItem item) => new
    {
        name = item.Name,
        value = item.Value,
        type = item.Type,
        variablesReference = item.Reference,
        evaluateName = item.EvaluateName,
    };

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static bool? Bool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) &&
           v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
