using System;
using System.IO;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Observability;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// トレースの記録・読取（観測性：<see cref="JsonlTraceSink"/> / <see cref="TraceReader"/>）の検証。
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
}
