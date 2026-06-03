using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.Core.Observability;

/// <summary>ツール1種別の利用統計。</summary>
public sealed record ToolStat(string Name, int Calls, int Errors, long TotalDurationMs)
{
    /// <summary>呼び出しあたりの平均所要(ms)。呼び出しが無ければ0。</summary>
    public double AvgDurationMs => Calls == 0 ? 0 : (double)TotalDurationMs / Calls;

    /// <summary>失敗率(0..1)。</summary>
    public double ErrorRate => Calls == 0 ? 0 : (double)Errors / Calls;
}

/// <summary>記録されたエラー1件（解析・改善提案の素材）。</summary>
public sealed record ErrorInfo(string Where, string Message);

/// <summary>
/// 1セッションのトレースから算出するメトリクス（設計書 §20.5・Phase B）。
/// ツール回数/失敗率/所要、反復数、承認待ち、time-to-first-token などを集計する。
/// </summary>
public sealed record SessionMetrics(
    string SessionId,
    string? Provider,
    int TurnCount,
    int TotalIterations,
    double AvgIterations,
    IReadOnlyList<ToolStat> ToolStats,
    int ToolCallCount,
    int ToolErrorCount,
    int SafetyBlockCount,
    int ApprovalCount,
    int ApprovalApprovedCount,
    double AvgApprovalWaitMs,
    double? AvgTimeToFirstTokenMs,
    double TotalTurnDurationMs,
    IReadOnlyList<ErrorInfo> Errors,
    long? TotalInputTokens,
    long? TotalOutputTokens)
{
    public static SessionMetrics Empty(string sessionId) => new(
        sessionId, null, 0, 0, 0, Array.Empty<ToolStat>(), 0, 0, 0, 0, 0, 0,
        null, 0, Array.Empty<ErrorInfo>(), null, null);

    /// <summary>トレースイベント列からメトリクスを算出する。</summary>
    public static SessionMetrics Compute(string sessionId, IReadOnlyList<TraceEvent> events)
    {
        if (events.Count == 0) return Empty(sessionId);

        string? provider = null;
        var turnCount = 0;
        var totalIterations = 0;
        var safetyBlocks = 0;
        var approvalCount = 0;
        var approvalApproved = 0;
        long approvalWaitSum = 0;
        double totalTurnDuration = 0;
        long inputTokens = 0, outputTokens = 0;
        var hasUsage = false;

        var toolCalls = new Dictionary<string, (int calls, int errors, long durMs)>();
        var errors = new List<ErrorInfo>();

        foreach (var ev in events)
        {
            var p = AsPayload(ev);
            switch (ev.Kind)
            {
                case TraceKinds.SessionStarted:
                case TraceKinds.TurnStarted:
                    provider ??= GetString(p, "provider");
                    break;

                case TraceKinds.ToolCompleted:
                {
                    var name = GetString(p, "name") ?? "(unknown)";
                    var isError = GetBool(p, "isError") ?? false;
                    var dur = GetLong(p, "durationMs") ?? 0;
                    var cur = toolCalls.TryGetValue(name, out var v) ? v : default;
                    toolCalls[name] = (cur.calls + 1, cur.errors + (isError ? 1 : 0), cur.durMs + dur);
                    break;
                }

                case TraceKinds.SafetyEvaluated:
                    if (GetBool(p, "blocked") == true) safetyBlocks++;
                    break;

                case TraceKinds.ApprovalResolved:
                    approvalCount++;
                    if (GetBool(p, "approved") == true) approvalApproved++;
                    approvalWaitSum += GetLong(p, "waitMs") ?? 0;
                    break;

                case TraceKinds.TurnCompleted:
                    turnCount++;
                    totalIterations += (int)(GetLong(p, "iterations") ?? 0);
                    totalTurnDuration += GetLong(p, "durationMs") ?? 0;
                    break;

                case TraceKinds.AiUsage:
                    hasUsage = true;
                    inputTokens += GetLong(p, "inputTokens") ?? 0;
                    outputTokens += GetLong(p, "outputTokens") ?? 0;
                    break;

                case TraceKinds.Error:
                    errors.Add(new ErrorInfo(
                        GetString(p, "where") ?? "(unknown)",
                        GetString(p, "message") ?? ""));
                    break;
            }
        }

        var toolStats = toolCalls
            .Select(kv => new ToolStat(kv.Key, kv.Value.calls, kv.Value.errors, kv.Value.durMs))
            .OrderByDescending(t => t.Calls)
            .ToList();

        return new SessionMetrics(
            SessionId: sessionId,
            Provider: provider,
            TurnCount: turnCount,
            TotalIterations: totalIterations,
            AvgIterations: turnCount == 0 ? 0 : (double)totalIterations / turnCount,
            ToolStats: toolStats,
            ToolCallCount: toolStats.Sum(t => t.Calls),
            ToolErrorCount: toolStats.Sum(t => t.Errors),
            SafetyBlockCount: safetyBlocks,
            ApprovalCount: approvalCount,
            ApprovalApprovedCount: approvalApproved,
            AvgApprovalWaitMs: approvalCount == 0 ? 0 : (double)approvalWaitSum / approvalCount,
            AvgTimeToFirstTokenMs: ComputeAvgTtft(events),
            TotalTurnDurationMs: totalTurnDuration,
            Errors: errors,
            TotalInputTokens: hasUsage ? inputTokens : null,
            TotalOutputTokens: hasUsage ? outputTokens : null);
    }

    /// <summary>
    /// time-to-first-token：各ターンの <c>turn.started</c> から最初の <c>ai.message</c>/<c>ai.tool_use</c>
    /// までの経過(ms)を封筒の <see cref="TraceEvent.Ts"/> で測り、平均する。
    /// </summary>
    private static double? ComputeAvgTtft(IReadOnlyList<TraceEvent> events)
    {
        var samples = new List<double>();
        foreach (var grp in events.Where(e => e.TurnId is not null).GroupBy(e => e.TurnId))
        {
            var ordered = grp.OrderBy(e => e.Seq).ToList();
            var start = ordered.FirstOrDefault(e => e.Kind == TraceKinds.TurnStarted);
            if (start is null) continue;
            var first = ordered.FirstOrDefault(e =>
                e.Kind is TraceKinds.AiMessage or TraceKinds.AiToolUse);
            if (first is null) continue;
            var ms = (first.Ts - start.Ts).TotalMilliseconds;
            if (ms >= 0) samples.Add(ms);
        }
        return samples.Count == 0 ? null : samples.Average();
    }

    // ===== payload(JsonElement) アクセサ =====

    private static JsonElement AsPayload(TraceEvent ev) =>
        ev.Payload is JsonElement e ? e : default;

    private static string? GetString(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long? GetLong(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : null;

    private static bool? GetBool(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
