using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>出自の項目を押した回数を日別に数える（§43.6 の測り直しの記録点）。</summary>
public sealed class ProvenanceUsageLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "loomo-provenance-usage-tests", Guid.NewGuid().ToString("N"), "u.json");
    private DateTime _clock = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Local);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch { }
    }

    private ProvenanceUsageLog New(string? path = null) => new(path, () => _clock);

    /// <summary>背景の保存が終わって <paramref name="done"/> を満たすまで読み直す。</summary>
    private async Task<ProvenanceUsageLog> ReloadUntil(Func<ProvenanceUsageLog, bool> done)
    {
        ProvenanceUsageLog? reloaded = null;
        for (var i = 0; i < 100 && (reloaded is null || !done(reloaded)); i++)
        {
            await Task.Delay(20);
            reloaded = New(_path);
        }
        return reloaded!;
    }

    [Fact]
    public void 同じ日に2回押すと1行で回数が2になる()
    {
        var log = New();
        log.Record("request.show");
        _clock = _clock.AddHours(5);
        log.Record("request.show");

        var entry = Assert.Single(log.Entries);
        Assert.Equal(new ProvenanceUsageLog.Entry("request.show", "2026-11-14", 2), entry);
    }

    [Fact]
    public void 日が変わると別の行になる()
    {
        var log = New();
        log.Record("request.show");
        _clock = _clock.AddDays(1);
        log.Record("request.show");

        Assert.Equal(["2026-11-14", "2026-11-15"], log.Entries.Select(e => e.Day));
        Assert.All(log.Entries, e => Assert.Equal(1, e.Count));
    }

    [Fact]
    public void 鍵が違えば同じ日でも別の行になる()
    {
        var log = New();
        log.Record("request.show");
        log.Record("places.jump");

        Assert.Equal(["request.show", "places.jump"], log.Entries.Select(e => e.Key));
    }

    [Fact]
    public void CountOfは全日の合計を返す()
    {
        var log = New();
        log.Record("request.places");
        log.Record("request.places");
        _clock = _clock.AddDays(3);
        log.Record("request.places");
        log.Record("request.copy");

        Assert.Equal(3, log.CountOf("request.places"));
        Assert.Equal(1, log.CountOf("request.copy"));
        Assert.Equal(0, log.CountOf("request.resume"));
    }

    [Fact]
    public void 空の鍵は無視する()
    {
        var log = New();
        log.Record("");
        log.Record(null!);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task ファイルへ保存して読み直せる()
    {
        var log = New(_path);
        log.Record("request.show");
        log.Record("request.show");
        _clock = _clock.AddDays(1);
        log.Record("request.diff");

        var reloaded = await ReloadUntil(r => r.Entries.Count == 2 && r.CountOf("request.show") == 2);

        Assert.Equal(
            [new ProvenanceUsageLog.Entry("request.show", "2026-11-14", 2), new ProvenanceUsageLog.Entry("request.diff", "2026-11-15", 1)],
            reloaded.Entries);

        // 読み直したものへ続けて数えられる。
        reloaded.Record("request.diff");
        Assert.Equal(2, reloaded.CountOf("request.diff"));
    }

    [Fact]
    public async Task MaxDaysより古い行は落ちる()
    {
        var log = New(_path);
        log.Record("request.show");                              // 11/14：落ちる
        _clock = _clock.AddDays(1);
        log.Record("request.resume");                            // 11/15：ちょうど MaxDays 前なので残る
        _clock = _clock.AddDays(ProvenanceUsageLog.MaxDays);
        log.Record("places.jump");

        Assert.Equal(["request.resume", "places.jump"], log.Entries.Select(e => e.Key));
        Assert.Equal(0, log.CountOf("request.show"));

        var reloaded = await ReloadUntil(r => r.CountOf("places.jump") == 1);
        Assert.Equal(["request.resume", "places.jump"], reloaded.Entries.Select(e => e.Key));
    }

    [Fact]
    public void 壊れたJSONは空として読む()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ これは JSON ではない [");

        var log = New(_path);

        Assert.Empty(log.Entries);
        log.Record("request.show");
        Assert.Equal(1, log.CountOf("request.show"));
    }

    [Fact]
    public void 保存先が無ければファイルを作らない()
    {
        var log = New();
        log.Record("request.show");

        Assert.False(File.Exists(_path));
    }
}
