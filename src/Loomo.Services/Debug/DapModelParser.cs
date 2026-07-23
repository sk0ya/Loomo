using System;
using System.Collections.Generic;
using System.Text.Json;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>
/// DAP 応答の JSON（<see cref="JsonElement"/>）を <c>Debug*</c> モデルへ変換する純関数パーサ。
/// <see cref="JsDebugService"/> が使う（<see cref="NetcoredbgDebugService"/> は同等のパースを private に
/// 抱えたままにしている——安定して動いているコードを動かさないため。将来の掃除でこちらへ寄せる）。
/// </summary>
internal static class DapModelParser
{
    /// <summary>stackTrace 応答 → フレーム一覧。</summary>
    public static IReadOnlyList<DebugStackFrame> ParseStackFrames(JsonElement? body)
    {
        var list = new List<DebugStackFrame>();
        if (body is not { ValueKind: JsonValueKind.Object } b ||
            !b.TryGetProperty("stackFrames", out var frames) || frames.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var f in frames.EnumerateArray())
        {
            int id = f.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number ? idp.GetInt32() : 0;
            var name = f.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
            int line = f.TryGetProperty("line", out var lp) && lp.ValueKind == JsonValueKind.Number ? lp.GetInt32() : 0;
            string? path = f.TryGetProperty("source", out var sp) && sp.ValueKind == JsonValueKind.Object &&
                           sp.TryGetProperty("path", out var pp) ? pp.GetString() : null;
            list.Add(new DebugStackFrame(id, name, path, line));
        }
        return list;
    }

    /// <summary>scopes 応答 → スコープ一覧。</summary>
    public static IReadOnlyList<DebugScope> ParseScopes(JsonElement? body)
    {
        var list = new List<DebugScope>();
        if (body is not { ValueKind: JsonValueKind.Object } b ||
            !b.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var s in scopes.EnumerateArray())
        {
            var name = s.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
            int vr = s.TryGetProperty("variablesReference", out var vp) && vp.ValueKind == JsonValueKind.Number ? vp.GetInt32() : 0;
            bool exp = s.TryGetProperty("expensive", out var ep) && ep.ValueKind == JsonValueKind.True;
            list.Add(new DebugScope(name, vr, exp));
        }
        return list;
    }

    /// <summary>variables 応答 → 変数一覧。</summary>
    public static IReadOnlyList<DebugVariable> ParseVariables(JsonElement? body)
    {
        var list = new List<DebugVariable>();
        if (body is not { ValueKind: JsonValueKind.Object } b ||
            !b.TryGetProperty("variables", out var vars) || vars.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var v in vars.EnumerateArray())
        {
            var name = v.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
            var value = v.TryGetProperty("value", out var vp) ? vp.GetString() ?? "" : "";
            var type = v.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            int vr = v.TryGetProperty("variablesReference", out var rp) && rp.ValueKind == JsonValueKind.Number ? rp.GetInt32() : 0;
            list.Add(new DebugVariable(name, value, type, vr));
        }
        return list;
    }

    /// <summary>threads 応答 → スレッド一覧。</summary>
    public static IReadOnlyList<DebugThread> ParseThreads(JsonElement? body)
    {
        var list = new List<DebugThread>();
        if (body is not { ValueKind: JsonValueKind.Object } b ||
            !b.TryGetProperty("threads", out var threads) || threads.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var t in threads.EnumerateArray())
        {
            int id = t.TryGetProperty("id", out var idp) && idp.ValueKind == JsonValueKind.Number ? idp.GetInt32() : 0;
            var name = t.TryGetProperty("name", out var np) ? np.GetString() ?? $"Thread {id}" : $"Thread {id}";
            list.Add(new DebugThread(id, name));
        }
        return list;
    }

    /// <summary>initialize 応答（capabilities）→ (setVariable 可, gotoTargets 可, stepInTargets 可, 例外フィルタ)。</summary>
    public static (bool SetVariable, bool Goto, bool StepInTargets, IReadOnlyList<DebugExceptionFilter> Filters)
        ParseCapabilities(JsonElement? caps)
    {
        if (caps is not { ValueKind: JsonValueKind.Object } c)
            return (false, false, false, Array.Empty<DebugExceptionFilter>());

        bool sv = c.TryGetProperty("supportsSetVariable", out var svp) && svp.ValueKind == JsonValueKind.True;
        bool gt = c.TryGetProperty("supportsGotoTargetsRequest", out var gtp) && gtp.ValueKind == JsonValueKind.True;
        bool st = c.TryGetProperty("supportsStepInTargetsRequest", out var stp) && stp.ValueKind == JsonValueKind.True;

        IReadOnlyList<DebugExceptionFilter> filters = Array.Empty<DebugExceptionFilter>();
        if (c.TryGetProperty("exceptionBreakpointFilters", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var list = new List<DebugExceptionFilter>();
            foreach (var f in arr.EnumerateArray())
            {
                var id = f.TryGetProperty("filter", out var fp) ? fp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                var label = f.TryGetProperty("label", out var lp) ? lp.GetString() ?? id : id;
                var def = f.TryGetProperty("default", out var dp) && dp.ValueKind == JsonValueKind.True;
                list.Add(new DebugExceptionFilter(id, label, def));
            }
            filters = list;
        }
        return (sv, gt, st, filters);
    }
}
