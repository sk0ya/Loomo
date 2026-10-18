using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>パレットで最近使ったコマンドを先頭に出す（§24.37）。</summary>
public sealed class PaletteCommandHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "loomo-palette-history-tests", Guid.NewGuid().ToString("N"), "h.json");
    private DateTime _clock = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch { }
    }

    private static PaletteCommand C(string title, string? id = null, string? shortcut = null)
        => new("移動", title, () => { }, shortcut, id);

    private PaletteCommandHistory New(string? path = null) => new(path, () => _clock = _clock.AddMinutes(1));

    [Fact]
    public void 最近使ったものが新しい順に先頭へ寄り_残りは元の順()
    {
        var a = C("A"); var b = C("B", shortcut: "Ctrl+B"); var c = C("C"); var d = C("D");
        var history = New();
        history.Record(c);
        history.Record(b);

        var ordered = history.Order([a, b, c, d]);

        Assert.Equal(["B", "C", "A", "D"], ordered.Select(x => x.Title));
        // キーの無いものには「最近」を添え、キーのあるものはキーのまま。
        Assert.Equal("Ctrl+B", ordered[0].RightText);
        Assert.Equal("最近", ordered[1].RightText);
    }

    [Fact]
    public void いま一覧に無いコマンドは飛ばし_出すのは5件まで()
    {
        var history = New();
        foreach (var t in new[] { "gone", "1", "2", "3", "4", "5", "6" })
            history.Record(C(t));
        var shown = new[] { "1", "2", "3", "4", "5", "6", "x" }.Select(t => C(t)).ToList();

        var ordered = history.Order(shown);

        Assert.Equal(["6", "5", "4", "3", "2", "1", "x"], ordered.Select(x => x.Title));
        Assert.Equal(PaletteCommandHistory.ShownCount, ordered.Count(x => x.RightText == "最近"));
    }

    [Fact]
    public void IDがあれば名前が変わっても同じコマンドとして数える()
    {
        var history = New();
        history.Record(C("集中にする", id: "mode.stage"));
        history.Record(C("分割にする", id: "mode.stage"));

        var entry = Assert.Single(history.Entries);
        Assert.Equal(2, entry.Count);
    }

    [Fact]
    public void 場所を指す行は記録しない()
    {
        var history = New();
        history.Record(new PaletteCommand("ファイル", "a.cs", () => { }) { Target = new PaletteTarget(@"C:\a.cs") });
        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task 再起動しても残る()
    {
        var history = New(_path);
        history.Record(C("A"));
        history.Record(C("B"));

        PaletteCommandHistory? reloaded = null;
        for (var i = 0; i < 50 && (reloaded?.Entries.Count ?? 0) < 2; i++)
        {
            await Task.Delay(20);
            reloaded = New(_path);
        }

        Assert.Equal(["B", "A"], reloaded!.Entries.Select(e => e.Key.Split('\u001f')[1]));
    }
}
