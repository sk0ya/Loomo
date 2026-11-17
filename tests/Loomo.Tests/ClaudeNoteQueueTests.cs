using System.Linq;
using System.Threading.Tasks;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.Tests;

/// <summary>次の頼みごとに添える知らせの待ち行列（§45.6 P3）。各知らせは 2 回まで添えられて消える（届かなかった場合の保険）・溜めすぎない・空の宛先は持たない。</summary>
public sealed class ClaudeNoteQueueTests
{
    private static ClaudeNote Note(string text) => new(ClaudeNote.KindMotionWorsened, text, DateTimeOffset.UnixEpoch);

    [Fact]
    public void 知らせは各_2回まで添えられて_使い切ると消え_別のセッションは残る()
    {
        var sut = new ClaudeNoteQueue();
        sut.Add("s1", Note("テストが落ちた"));
        sut.Add("s2", Note("別のセッション"));

        Assert.Equal(["テストが落ちた"], sut.Take("s1").Select(n => n.Text));
        Assert.Equal(["テストが落ちた"], sut.Take("s1").Select(n => n.Text));   // 1 回目が届かなかった場合の保険
        Assert.Empty(sut.Take("s1"));                     // 使い切った
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
    public async Task 同じ待ち行列を別のスレッドから同時に触っても_取りこぼさず二重に取らない()
    {
        var sut = new ClaudeNoteQueue();
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
                sut.Add("s1", Note("x"));
        })).ToArray();
        await Task.WhenAll(writers);

        // 最大 8 件で止まる（溜めすぎない）。2 回までは同じ 8 件が返り、そのあとは空。
        Assert.Equal(ClaudeNoteQueue.MaxPerSession, sut.Take("s1").Count);
        Assert.Equal(ClaudeNoteQueue.MaxPerSession, sut.Take("s1").Count);
        Assert.Empty(sut.Take("s1"));
    }

    [Fact]
    public void 見るだけでは消えず_取り消すと外れ_空になったセッションは一覧から消える()
    {
        var sut = new ClaudeNoteQueue();
        var a = Note("a");
        var b = Note("b");
        sut.Add("s1", a);
        sut.Add("s1", b);

        Assert.Equal(["a", "b"], sut.Peek("s1").Select(n => n.Text));
        Assert.Equal(2, sut.Count("s1"));                  // Peek は消さない
        Assert.Equal(["s1"], sut.Sessions());

        Assert.True(sut.Discard("s1", a));
        Assert.False(sut.Discard("s1", a));                // 二度目は無い
        Assert.Equal(["b"], sut.Peek("s1").Select(n => n.Text));

        Assert.True(sut.Discard("s1", b));
        Assert.Empty(sut.Sessions());                      // 空になったセッションは一覧にも出ない
    }

    [Fact]
    public void 場所に宛てた指摘は_ファイルごとに_大文字小文字を問わず取り出せ_取り出しても消えない()
    {
        var sut = new ClaudeNoteQueue();
        sut.Add("s1", new ClaudeNote(ClaudeNote.KindReview, "a", DateTimeOffset.UnixEpoch, Path: @"C:\w\a.cs", Line: 3));
        sut.Add("s2", new ClaudeNote(ClaudeNote.KindReview, "b", DateTimeOffset.UnixEpoch, Path: @"c:\W\A.cs", Line: 9));
        sut.Add("s2", new ClaudeNote(ClaudeNote.KindReview, "c", DateTimeOffset.UnixEpoch, Path: @"C:\w\other.cs", Line: 1));
        sut.Add("s1", Note("場所なし"));

        Assert.Equal(["a", "b"], sut.ReviewNotesFor(@"C:\w\a.cs").Select(n => n.Text));
        Assert.Equal(["a", "b"], sut.ReviewNotesFor(@"C:\w\a.cs").Select(n => n.Text));   // 見るだけ
        Assert.Empty(sut.ReviewNotesFor(@"C:\w\none.cs"));
    }
}