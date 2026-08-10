using System.Globalization;
using System.Windows;
using System.Windows.Media;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ページ幅の絞り込み（等幅セル数で候補を選んでから実測）が、<b>全行を実測した場合と同じ幅</b>を返すこと。
/// ここが狂うとページ幅が足りず、本文だけが折り返して折り返さない行番号ガターと1行ずつずれる
/// ——行番号が信用できなくなる不具合になるので、速度のための近道が結果を変えていないことを固定する。
/// </summary>
public class MonospacePageWidthTests
{
    private static readonly Typeface Mono = new(
        new FontFamily("Cascadia Mono, Consolas"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private const double FontSize = 12.0;
    private const double PageWidthPadding = 24.0;
    private const double MinWidth = 200.0;

    /// <summary>比較対象＝以前の実装（全行に FormattedText を当てる）。</summary>
    private static double MeasureEveryLine(IEnumerable<string> lines)
    {
        var max = 0.0;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var formatted = new FormattedText(
                line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Mono, FontSize, Brushes.Black, 1.0);
            if (formatted.WidthIncludingTrailingWhitespace > max)
                max = formatted.WidthIncludingTrailingWhitespace;
        }
        return Math.Max(MinWidth, max + PageWidthPadding);
    }

    private static double Measure(IEnumerable<string> lines)
        => MonospacePageWidth.Measure(lines, Mono, FontSize, pixelsPerDip: 1.0);

    public static TheoryData<string, string[]> Corpus() => new()
    {
        { "空", [] },
        { "空行だけ", ["", "", ""] },
        { "短いASCII", ["a", "bb", "ccc"] },
        {
            "最長がASCII", Lines(300, i => new string('x', i % 90))
        },
        {
            // 全角は等幅フォントでも2セル幅。セル数の重み付けを誤ると候補から漏れる。
            "日本語混在", Lines(300, i => i % 7 == 0
                ? new string('あ', i % 40) + " // 全角コメント"
                : new string('x', i % 60))
        },
        {
            // タブは1文字なのに描画幅が広い。文字数で順位付けすると本当の最長を取り逃す。
            "タブ混在", Lines(300, i => i % 5 == 0
                ? new string('\t', i % 12) + "tabbed"
                : new string('y', i % 50))
        },
        {
            // 最長行が末尾にある（先頭16本を拾って打ち切る実装だと落ちる）
            "最長が末尾", [.. Lines(200, i => new string('z', i % 20)), new string('w', 400)]
        },
        {
            // 候補が僅差でひしめく（順位付けの誤差が結果に出やすい）
            "僅差", Lines(200, i => new string('m', 80 + i % 3))
        },
        { "末尾空白", ["abc", "abc      ", "ab"] },
        {
            // 実バグ：全角は ASCII の1.39倍しか無いのに2セルと数えていたため、全角行が候補枠を
            // 埋め尽くし、本当に一番長い ASCII 行が押し出されて幅が 656.7px→513.5px に不足した。
            "全角行が候補を埋める＋ASCIIが最長",
            [.. Enumerable.Repeat(new string('あ', 50), 20), new string('x', 90)]
        },
        {
            // 罫線・矢印は等幅フォントで ASCII と同じ幅（実測 7.03px）。「非 ASCII＝広い」は誤り。
            "罫線行が候補を埋める＋ASCIIが最長",
            [.. Enumerable.Repeat(new string('─', 60), 20), new string('x', 90)]
        },
        {
            "絵文字混在",
            [.. Enumerable.Repeat(string.Concat(Enumerable.Repeat("😀", 30)), 20), new string('x', 90)]
        },
        {
            "タブ行が候補を埋める＋ASCIIが最長",
            [.. Enumerable.Repeat(new string('\t', 10), 20), new string('x', 90)]
        },
    };

    private static string[] Lines(int count, Func<int, string> build)
        => Enumerable.Range(0, count).Select(build).ToArray();

    [Theory]
    [MemberData(nameof(Corpus))]
    public void 候補を絞った計測は全行実測と同じ幅になる(string label, string[] lines)
    {
        Assert.Equal(MeasureEveryLine(lines), Measure(lines), precision: 6);
        Assert.False(string.IsNullOrEmpty(label));
    }

    /// <summary>
    /// 文字種と長さをランダムに混ぜた行集合でも、全行実測と1pxも違わないこと。
    /// 「全角は2セル」のような重み付けの思い込みは、たまたま最長行が最大セル数でもある例では
    /// 露見しない——実際、この近道は日本語混在の固定例では通るのに実バグを抱えていた。
    /// 種を固定した乱択で、重み付けが順位を狂わせる組み合わせを面で潰す。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void 文字種を混ぜた乱択でも全行実測と一致する(int seed)
    {
        var random = new Random(seed);
        // ASCII と同幅のもの（罫線・矢印）、1.39倍の全角、2.3倍の絵文字、6.8倍のタブを混ぜる
        string[] alphabets = ["x", "あ", "─", "→", "\t", "😀", "aあ", "─x"];

        for (var trial = 0; trial < 60; trial++)
        {
            var lines = new string[random.Next(1, 60)];
            for (var i = 0; i < lines.Length; i++)
            {
                var unit = alphabets[random.Next(alphabets.Length)];
                lines[i] = string.Concat(Enumerable.Repeat(unit, random.Next(0, 60)));
            }

            Assert.Equal(MeasureEveryLine(lines), Measure(lines), precision: 6);
        }
    }

    [Fact]
    public void 全行が同じ文字種なら候補は片方の尺度ぶんに収まる()
    {
        // ASCII だけなら2つの尺度が同じ順位を出すので、和集合は重ならず16本のまま
        var lines = Lines(200, i => new string('x', i + 1));

        Assert.Equal(MonospacePageWidth.MeasuredCandidates, MonospacePageWidth.LongestCandidates(lines).Count);
    }

    [Fact]
    public void 候補は2つの尺度ぶんを超えて増えない()
    {
        // 尺度どうしが最も食い違う組み合わせ（全角だけの行と ASCII だけの行）でも上限は倍まで
        var lines = Lines(400, i => i % 2 == 0
            ? new string('あ', i + 1)
            : new string('x', i + 1));

        Assert.InRange(
            MonospacePageWidth.LongestCandidates(lines).Count, 1, MonospacePageWidth.MeasuredCandidates * 2);
    }

    [Fact]
    public void セル数で埋まっても文字数側が最長のASCII行を拾う()
    {
        var longest = new string('x', 90);
        string[] lines = [.. Enumerable.Repeat(new string('あ', 50), 20), longest];

        Assert.Contains(longest, MonospacePageWidth.LongestCandidates(lines));
    }

    [Fact]
    public void 空行は候補にしない()
        => Assert.Empty(MonospacePageWidth.LongestCandidates(["", "", ""]));

    [Fact]
    public void 全角はASCIIより多くのセルを占める()
        => Assert.True(MonospacePageWidth.CellCount("あい") > MonospacePageWidth.CellCount("ai"));

    [Fact]
    public void タブは1文字より広いものとして数える()
        => Assert.True(MonospacePageWidth.CellCount("\t") > MonospacePageWidth.CellCount(" "));

    [Fact]
    public void サロゲートペアは1文字として数える()
    {
        Assert.Equal(2, MonospacePageWidth.CodePointCount("😀😀"));
        Assert.Equal(MonospacePageWidth.CodePointCount("xx"), MonospacePageWidth.CodePointCount("😀😀"));
    }
}
