using System.Linq;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>次の頼みごとに添える知らせの待ち行列（§45.6 P3）。セッションごとに一度だけ取れる・溜めすぎない・空の宛先は持たない。</summary>
public sealed class ClaudeNoteQueueTests
{
    private static ClaudeNote Note(string text) => new(ClaudeNote.KindMotionWorsened, text, DateTimeOffset.UnixEpoch);

    [Fact]
    public void 知らせはセッションごとに_一度だけ取れて_取ったら消える()
    {
        var sut = new ClaudeNoteQueue();
        sut.Add("s1", Note("テストが落ちた"));
        sut.Add("s2", Note("別のセッション"));

        Assert.Equal(["テストが落ちた"], sut.Take("s1").Select(n => n.Text));
        Assert.Empty(sut.Take("s1"));                     // 二度目は無い
        Assert.Equal(1, sut.Count("s2"));                 // 別のセッションのものは残る
    }

    [Fact]
    public void 溜めすぎない_古いものから捨てる()
    {
        var sut = new ClaudeNoteQueue();
        for (var i = 0; i < ClaudeNoteQueue.MaxPerSession + 3; i++)
            sut.Add("s1", Note($"n{i}"));

        var taken = sut.Take("s1").Select(n => n.Text).ToList();
        Assert.Equal(ClaudeNoteQueue.MaxPerSession, taken.Count);
        Assert.Equal("n3", taken[0]);                     // 先頭 3 件は捨てた
        Assert.Equal($"n{ClaudeNoteQueue.MaxPerSession + 2}", taken[^1]);
    }

    [Fact]
    public void 宛先が無いか空の知らせは持たない()
    {
        var sut = new ClaudeNoteQueue();
        sut.Add("", Note("宛先なし"));
        sut.Add("s1", Note("   "));
        Assert.Equal(0, sut.Count(""));
        Assert.Equal(0, sut.Count("s1"));
    }

    [Fact]
    public void 添える本文は_Loomo_からの知らせだと分かる見出しと_箇条書き()
    {
        var text = ClaudeNoteQueue.FormatContext([Note("動きが悪くなった"), Note("指摘：この行")]);
        Assert.StartsWith("Loomo（この部屋）からの知らせです", text);
        Assert.Contains("- 動きが悪くなった", text);
        Assert.Contains("- 指摘：この行", text);
    }

    [Fact]
    public void 同じ待ち行列を別のスレッドから同時に触っても_取りこぼさず二重に取らない()
    {
        var sut = new ClaudeNoteQueue();
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
                sut.Add("s1", Note("x"));
        })).ToArray();
        Task.WaitAll(writers);

        // 最大 8 件で止まる（溜めすぎない）。取り合っても両方が同じものを返すことはない。
        var first = sut.Take("s1");
        Assert.Equal(ClaudeNoteQueue.MaxPerSession, first.Count);
        Assert.Empty(sut.Take("s1"));
    }
}
