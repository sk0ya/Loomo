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

    [Fact]
    public void Record_replaces_redirect_hop_with_its_destination_and_persists_target()
    {
        var now = new DateTime(2026, 10, 20, 10, 0, 0);
        var store = new TrailStore(_path);
        var handler = new TrailRecordHandler(store, () => now);
        handler.SetWorkspace("workspace");
        var hop = handler.Record(BrowserRequest("https://www.bing.com/ck/a?u=x", "bing.com"));
        now = now.AddSeconds(1);

        var destination = handler.Record(BrowserRequest(
            "https://example.com/page", "Example", replaces: "https://www.bing.com/ck/a?u=x"));

        Assert.False(destination!.Added);
        Assert.Same(hop!.Entry, destination.Entry);
        Assert.Equal("https://example.com/page", destination.Entry.Target);
        Assert.Equal("Example", destination.Entry.Label);
        var row = Assert.Single(store.LoadDay("workspace", DateOnly.FromDateTime(now)));
        Assert.Equal("https://example.com/page", row.Target);
        Assert.Equal("Example", row.Label);
    }

    [Fact]
    public void Record_does_not_replace_when_latest_point_is_not_the_redirect_hop()
    {
        var now = new DateTime(2026, 10, 20, 10, 0, 0);
        var store = new TrailStore(_path);
        var handler = new TrailRecordHandler(store, () => now);
        handler.SetWorkspace("workspace");
        handler.Record(BrowserRequest("https://www.bing.com/ck/a?u=x", "bing.com"));
        handler.Record(Request("C:\\work\\a.cs", 1));

        var destination = handler.Record(BrowserRequest(
            "https://example.com/page", "Example", replaces: "https://www.bing.com/ck/a?u=x"));

        Assert.True(destination!.Added);
        Assert.Equal(3, store.LoadDay("workspace", DateOnly.FromDateTime(now)).Count);
    }

    private static TrailRecordRequest BrowserRequest(string url, string label, string? replaces = null) => new(
        TrailEntryKind.Browser, url, label, -1, -1, DisplayMode.Layout, null, null, replaces);

    private static TrailRecordRequest Request(string path, int line) => new(
        TrailEntryKind.File, path, "a.cs", line, 0, DisplayMode.Layout, null, null);
}
