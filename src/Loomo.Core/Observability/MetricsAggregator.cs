using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Core.Observability;

/// <summary>横断（全セッション）集計の結果（設計書 §20.5）。</summary>
public sealed record CrossSessionMetrics(
    int SessionCount,
    int TurnCount,
    double AvgIterations,
    IReadOnlyList<ToolStat> ToolStats,
    int ToolCallCount,
    int ToolErrorCount,
    int SafetyBlockCount,
    int ApprovalCount,
    double AvgApprovalWaitMs,
    double? AvgTimeToFirstTokenMs,
    IReadOnlyList<KeyValuePair<string, int>> ErrorsByWhere,
    long? TotalInputTokens,
    long? TotalOutputTokens)
{
    public static readonly CrossSessionMetrics Empty = new(
        0, 0, 0, Array.Empty<ToolStat>(), 0, 0, 0, 0, 0, null,
        Array.Empty<KeyValuePair<string, int>>(), null, null);
}

/// <summary>複数セッションの <see cref="SessionMetrics"/> を横断集計する。</summary>
public static class MetricsAggregator
{
    /// <summary>ツール統計は失敗率の高い順、エラーは <c>where</c> 別件数の多い順に並べて返す。</summary>
    public static CrossSessionMetrics Compute(IReadOnlyCollection<SessionMetrics> sessions)
    {
        if (sessions.Count == 0) return CrossSessionMetrics.Empty;

        var turnCount = sessions.Sum(s => s.TurnCount);
        var totalIterations = sessions.Sum(s => s.TotalIterations);

        // ツール別に合算（calls / errors / 所要）。
        var tools = new Dictionary<string, (int calls, int errors, long durMs)>();
        foreach (var t in sessions.SelectMany(s => s.ToolStats))
        {
            var cur = tools.TryGetValue(t.Name, out var v) ? v : default;
            tools[t.Name] = (cur.calls + t.Calls, cur.errors + t.Errors, cur.durMs + t.TotalDurationMs);
        }
        var toolStats = tools
            .Select(kv => new ToolStat(kv.Key, kv.Value.calls, kv.Value.errors, kv.Value.durMs))
            .OrderByDescending(t => t.ErrorRate)
            .ThenByDescending(t => t.Calls)
            .ToList();

        var errorsByWhere = sessions
            .SelectMany(s => s.Errors)
            .GroupBy(e => e.Where)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .OrderByDescending(kv => kv.Value)
            .ToList();

        var approvalCount = sessions.Sum(s => s.ApprovalCount);
        var approvalWaitSum = sessions.Sum(s => s.AvgApprovalWaitMs * s.ApprovalCount);

        var ttftSamples = sessions
            .Where(s => s.AvgTimeToFirstTokenMs is not null && s.TurnCount > 0)
            .ToList();
        double? avgTtft = ttftSamples.Count == 0
            ? null
            : ttftSamples.Sum(s => s.AvgTimeToFirstTokenMs!.Value * s.TurnCount) / ttftSamples.Sum(s => s.TurnCount);

        var hasUsage = sessions.Any(s => s.TotalInputTokens is not null);

        return new CrossSessionMetrics(
            SessionCount: sessions.Count,
            TurnCount: turnCount,
            AvgIterations: turnCount == 0 ? 0 : (double)totalIterations / turnCount,
            ToolStats: toolStats,
            ToolCallCount: toolStats.Sum(t => t.Calls),
            ToolErrorCount: toolStats.Sum(t => t.Errors),
            SafetyBlockCount: sessions.Sum(s => s.SafetyBlockCount),
            ApprovalCount: approvalCount,
            AvgApprovalWaitMs: approvalCount == 0 ? 0 : approvalWaitSum / approvalCount,
            AvgTimeToFirstTokenMs: avgTtft,
            ErrorsByWhere: errorsByWhere,
            TotalInputTokens: hasUsage ? sessions.Sum(s => s.TotalInputTokens ?? 0) : null,
            TotalOutputTokens: hasUsage ? sessions.Sum(s => s.TotalOutputTokens ?? 0) : null);
    }
}
