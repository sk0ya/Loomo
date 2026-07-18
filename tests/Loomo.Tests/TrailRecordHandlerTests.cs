using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

public sealed class TrailRecordHandlerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"loomo-trail-handler-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public void Record_deduplicates_consecutive_file_location_and_persists_update()
    {
        var now = new DateTime(2026, 7, 18, 10, 0, 0);
        var store = new TrailStore(_path);
        var handler = new TrailRecordHandler(store, () => now);
        handler.SetWorkspace("workspace");
        var first = handler.Record(Request("C:\\work\\a.cs", 1));
        now = now.AddMinutes(5);

        var second = handler.Record(Request("C:\\WORK\\a.cs", 7));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first!.Added);
        Assert.False(second!.Added);
        Assert.Same(first.Entry, second.Entry);
        Assert.Equal(7, second.Entry.Line);
        Assert.Single(store.LoadDay("workspace", DateOnly.FromDateTime(now)));
    }

    private static TrailRecordRequest Request(string path, int line) => new(
        TrailEntryKind.File, path, "a.cs", line, 0, DisplayMode.Layout, null, null);
}
