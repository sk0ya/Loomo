using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Observability;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ログ解析層（観測性 Phase B：<see cref="TraceReader"/> / <see cref="SessionMetrics"/> /
/// <see cref="MetricsAggregator"/>）と改善提案ヘルパ（<see cref="ImprovementAdvisor"/>）の検証。
/// </summary>
public class TraceAnalysisTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "loomo-an-" + Guid.NewGuid().ToString("N"));

    /// <summary>JsonlTraceSink で書いたものを TraceReader で読み戻し、往復が壊れないこと。</summary>
    [Fact]
    public async Task TraceReader_round_trips_written_events()
    {
        var dir = TempDir();
        var sink = new JsonlTraceSink(dir);
        sink.Record("s1", "t1", TraceKinds.TurnStarted, new { userInput = "hi", provider = "Local" });
        sink.Record("s1", "t1", TraceKinds.AiMessage, new { fullText = "ok" });
        await sink.DisposeAsync();

        var reader = new TraceReader(dir);
        var events = reader.Read("s1");
        Assert.Equal(2, events.Count);
        Assert.Equal(TraceKinds.TurnStarted, events[0].Kind);

        var list = reader.List();
        Assert.Single(list);
        Assert.Equal("s1", list[0].SessionId);

        Directory.Delete(dir, recursive: true);
    }

    /// <summary>ツール統計・反復・承認・安全ブロック・エラー・TTFT を算出できること。</summary>
    [Fact]
    public async Task SessionMetrics_aggregates_tools_approvals_and_errors()
    {
        var dir = TempDir();
        var sink = new JsonlTraceSink(dir);
        sink.Record("m", null, TraceKinds.SessionStarted, new { provider = "Local" });
        sink.Record("m", "t1", TraceKinds.TurnStarted, new { provider = "Local" });
        sink.Record("m", "t1", TraceKinds.AiMessage, new { fullText = "考え中" });
        sink.Record("m", "t1", TraceKinds.SafetyEvaluated, new { tool = "pwsh", blocked = true, reason = "danger" });
        sink.Record("m", "t1", TraceKinds.ApprovalResolved, new { tool = "propose_edit", approved = true, waitMs = 1000 });
        sink.Record("m", "t1", TraceKinds.ToolCompleted, new { toolUseId = "u1", name = "read_file", isError = false, durationMs = 100 });
        sink.Record("m", "t1", TraceKinds.ToolCompleted, new { toolUseId = "u2", name = "read_file", isError = true, durationMs = 300 });
        sink.Record("m", "t1", TraceKinds.Error, new { message = "boom", where = "tool.args" });
        sink.Record("m", "t1", TraceKinds.TurnCompleted, new { finalText = "done", iterations = 3, durationMs = 5000 });
        await sink.DisposeAsync();

        var m = SessionMetrics.Compute("m", new TraceReader(dir).Read("m"));

        Assert.Equal("Local", m.Provider);
        Assert.Equal(1, m.TurnCount);
        Assert.Equal(3, m.TotalIterations);
        Assert.Equal(2, m.ToolCallCount);
        Assert.Equal(1, m.ToolErrorCount);
        Assert.Equal(1, m.SafetyBlockCount);
        Assert.Equal(1, m.ApprovalCount);
        Assert.Equal(1, m.ApprovalApprovedCount);
        Assert.Equal(1000, m.AvgApprovalWaitMs);

        var readFile = Assert.Single(m.ToolStats);
        Assert.Equal("read_file", readFile.Name);
        Assert.Equal(2, readFile.Calls);
        Assert.Equal(1, readFile.Errors);
        Assert.Equal(200, readFile.AvgDurationMs); // (100+300)/2
        Assert.Single(m.Errors);
        Assert.NotNull(m.AvgTimeToFirstTokenMs);
        Assert.True(m.AvgTimeToFirstTokenMs >= 0);

        Directory.Delete(dir, recursive: true);
    }

    /// <summary>横断集計はツールを失敗率の高い順に並べること。</summary>
    [Fact]
    public void MetricsAggregator_orders_tools_by_error_rate()
    {
        var s1 = SessionMetrics.Compute("a", Array.Empty<TraceEvent>()) with
        {
            ToolStats = new[]
            {
                new ToolStat("safe_tool", 10, 0, 1000),
                new ToolStat("flaky_tool", 4, 3, 800),
            },
            TurnCount = 2,
        };

        var cross = MetricsAggregator.Compute(new[] { s1 });

        Assert.Equal(2, cross.ToolStats.Count);
        Assert.Equal("flaky_tool", cross.ToolStats[0].Name); // 失敗率が高い方が先頭
        Assert.Equal(14, cross.ToolCallCount);
        Assert.Equal(3, cross.ToolErrorCount);
        Assert.Equal(1, cross.SessionCount);
    }

    /// <summary>失敗ツール（引数つき）と記録エラーをサンプルとして抽出すること。</summary>
    [Fact]
    public async Task BuildFailureSamples_collects_failed_tools_and_errors()
    {
        var dir = TempDir();
        var sink = new JsonlTraceSink(dir);
        sink.Record("f", "t1", TraceKinds.AiToolUse, new { toolUseId = "u1", name = "pwsh", argsJson = "{\"command\":\"rm -rf /\"}" });
        sink.Record("f", "t1", TraceKinds.ToolCompleted, new { toolUseId = "u1", name = "pwsh", isError = true, durationMs = 10 });
        sink.Record("f", "t1", TraceKinds.Error, new { message = "解析失敗", where = "tool.args" });
        await sink.DisposeAsync();

        var samples = ImprovementAdvisor.BuildFailureSamples(new TraceReader(dir).Read("f"));

        Assert.Equal(2, samples.Count);
        Assert.Contains(samples, s => s.Contains("pwsh") && s.Contains("rm -rf /"));
        Assert.Contains(samples, s => s.Contains("解析失敗"));

        Directory.Delete(dir, recursive: true);
    }
}
