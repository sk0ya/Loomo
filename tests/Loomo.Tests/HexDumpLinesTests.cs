using System.Collections;
using System.Linq;
using System.Text;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Hex フォールバック表示の遅延整形（<see cref="HexDumpLines"/>）の検証。
/// 行数・オフセット・16進/ASCII 整形と、UI 仮想化が依存する IList 経路（Count＋インデクサ）を確認する。
/// </summary>
public class HexDumpLinesTests
{
    [Fact]
    public void Count_RoundsUpToSixteenBytePerLine()
    {
        static int Lines(int byteCount) => new HexDumpLines(new byte[byteCount]).Count;

        Assert.Equal(0, Lines(0));
        Assert.Equal(1, Lines(1));
        Assert.Equal(1, Lines(16));
        Assert.Equal(2, Lines(17));
        Assert.Equal(2, Lines(32));
    }

    [Fact]
    public void FormatsOffsetHexAndAscii_FullLine()
    {
        // "ABCDEFGHIJKLMNOP" = 0x41..0x50
        var bytes = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP");
        var line = new HexDumpLines(bytes)[0];

        Assert.Equal(
            "00000000  41 42 43 44 45 46 47 48  49 4a 4b 4c 4d 4e 4f 50  |ABCDEFGHIJKLMNOP|",
            line);
    }

    [Fact]
    public void PartialLastLine_PadsHexAndTrimsAscii()
    {
        var bytes = Encoding.ASCII.GetBytes("Hi");
        var line = new HexDumpLines(bytes)[0];

        // 2 バイトだけ。残りの16進は空白で詰め、ASCII は 2 文字のみ。
        Assert.StartsWith("00000000  48 69 ", line);
        Assert.EndsWith("|Hi|", line);
    }

    [Fact]
    public void NonPrintableBytes_RenderAsDot()
    {
        var bytes = new byte[] { 0x00, 0x41, 0x7f, 0xff };
        var line = new HexDumpLines(bytes)[0];

        Assert.EndsWith("|.A..|", line);
    }

    [Fact]
    public void SecondLine_OffsetIsSixteen()
    {
        var bytes = new byte[20];
        var line = new HexDumpLines(bytes)[1];

        Assert.StartsWith("00000010  ", line);
    }

    [Fact]
    public void ExposedAsIList_ForUiVirtualization()
    {
        IList list = new HexDumpLines(new byte[40]);

        // 仮想化は Count とインデクサだけで回る。書き換えは拒否。
        Assert.Equal(3, list.Count);
        Assert.True(list.IsFixedSize);
        Assert.True(list.IsReadOnly);
        Assert.NotNull(list[0]);
        Assert.Throws<System.NotSupportedException>(() => list.Add("x"));
    }
}
