using System;
using System.Linq;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Files;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>文字コードの調査（§23.3.4）：文字コード・BOM・改行・見えない文字・文字化けの跡と、パレットへの並べ方。</summary>
public class EncodingInspectorTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    [Fact]
    public void Reports_encoding_bom_and_line_endings()
    {
        var report = EncodingInspector.Inspect([.. Encoding.UTF8.GetPreamble(), .. Utf8NoBom.GetBytes("a\r\nb\nc")]);

        Assert.Equal("UTF-8 (BOM)", report.EncodingName);
        Assert.True(report.HasBom);
        Assert.Equal((1, 1, 0), (report.CrLf, report.Lf, report.Cr));
        Assert.True(report.HasMixedLineEndings);
        Assert.Equal("混在（CRLF 1 / LF 1）", report.LineEndingSummary);
        Assert.False(report.EndsWithNewline);
        Assert.Empty(report.Findings);   // 先頭の BOM は見つけたものに数えない
    }

    [Fact]
    public void Reports_shift_jis()
    {
        var report = EncodingInspector.Inspect(TextFileEncoding.ShiftJis.GetBytes("設定\r\n値\r\n"));
        Assert.Equal("Shift_JIS", report.EncodingName);
        Assert.Equal("CRLF", report.LineEndingSummary);
        Assert.True(report.EndsWithNewline);
    }

    [Fact]
    public void Finds_invisible_and_bidi_characters_with_positions()
    {
        var text = "ok\nint\u3000x = 1;\nzero\u200Bwidth\nif (a\u202E) {}\nbell\u0007\nmid\uFEFFbom\n";
        var report = EncodingInspector.Inspect(Utf8NoBom.GetBytes(text));

        Assert.Equal(new[]
        {
            (2, 4, EncodingFindingKind.Invisible),
            (3, 5, EncodingFindingKind.Invisible),
            (4, 6, EncodingFindingKind.Bidi),
            (5, 5, EncodingFindingKind.Invisible),
            (6, 4, EncodingFindingKind.Invisible),
        }, report.Findings.Select(f => (f.Line, f.Column, f.Kind)));
        Assert.Equal("L2:4 全角スペース", EncodingInspector.Label(report.Findings[0]));
    }

    [Theory]
    [InlineData("縺ゅ≠繧九ヵ繧｡繧､繝ｫ")]        // UTF-8 の日本語を Shift_JIS として読んだ形
    [InlineData("cafÃ© au lait")]               // UTF-8 の é を Latin-1 として読んだ形
    [InlineData("readâ€™s")]                    // UTF-8 の ’ を Windows-1252 として読んだ形
    [InlineData("壊れた\uFFFD文字")]             // 置換文字
    public void Finds_mojibake(string line)
    {
        var report = EncodingInspector.Inspect(Utf8NoBom.GetBytes(line + "\n"));
        Assert.Contains(report.Findings, f => f.Kind == EncodingFindingKind.Mojibake);
    }

    [Theory]
    [InlineData("縦横の繧は普通の文章に一度だけ")]   // 印の字が1つ出ただけでは疑わない
    [InlineData("não é ação")]                      // ポルトガル語の正しい文字
    [InlineData("👨\u200D👩\u200D👧 family")]                      // 絵文字の ZWJ は見えない文字に数えない
    public void Does_not_flag_ordinary_text(string line)
        => Assert.Empty(EncodingInspector.Inspect(Utf8NoBom.GetBytes(line + "\n")).Findings);

    [Fact]
    public void Binary_is_not_decoded()
        => Assert.False(EncodingInspector.Inspect([0x00, 0x01, 0xFF, 0xFE, 0x00]).IsDecoded);

    [Fact]
    public void Findings_are_capped()
    {
        var report = EncodingInspector.Inspect(Utf8NoBom.GetBytes(new string('\u3000', EncodingInspector.MaxFindings + 10)));
        Assert.Equal(EncodingInspector.MaxFindings, report.Findings.Count);
        Assert.True(report.Truncated);
    }

    // ===== 変換（保存するときの形をエディタに変えてもらう）=====

    [Fact]
    public void Palette_offers_only_conversions_that_change_something()
    {
        static string[] Titles(byte[] bytes) => EncodingInspectionPalette
            .Build(EncodingInspector.Inspect(bytes), "a.txt", false, _ => () => { }, _ => () => { })
            .Where(i => i.Category.EndsWith("の変換", StringComparison.Ordinal)).Select(i => i.Title).ToArray();

        // UTF-8・LF だけ：BOM 付きと CRLF への変換だけを出す
        Assert.Equal(new[] { "UTF-8（BOM 付き）で保存する", "改行を CRLF にそろえて保存する" },
            Titles(Utf8NoBom.GetBytes("a\nb\n")));
        // Shift_JIS・混在：全部出す
        Assert.Equal(4, Titles(TextFileEncoding.ShiftJis.GetBytes("設定\r\n値\n")).Length);
        // 判定できないファイルには変換を出さない
        Assert.Empty(Titles([0x00, 0xFF]));
    }

    [Fact]
    public void Palette_conversion_runs_the_requested_conversion()
    {
        TextConversion? requested = null;
        var items = EncodingInspectionPalette.Build(EncodingInspector.Inspect(Utf8NoBom.GetBytes("a\r\n")), "a.txt", false,
            _ => () => { }, conversion => () => requested = conversion);

        items.Single(i => i.Title == "改行を LF にそろえて保存する").Execute();
        Assert.Equal(TextConversion.ToLf, requested);
    }

    // ===== フッターから：エディタの保存形式を選ぶ =====

    [Fact]
    public void Footer_line_endings_mark_the_current_one()
    {
        string? selected = null;
        var items = EditorFileFormatPalette.BuildLineEndings("dos", name => () => selected = name);

        Assert.Equal(new[] { "LF", "CRLF", "CR" }, items.Select(i => i.Title));
        Assert.Equal("いま", items[1].Badge);
        items[1].Execute();                 // いまの形を選んでも何もしない
        Assert.Null(selected);
        items[0].Execute();
        Assert.Equal("unix", selected);     // エディタの名前（SetFileFormat に渡す値）で返す
    }

    [Fact]
    public void Footer_encodings_use_editor_names()
    {
        string? selected = null;
        var items = EditorFileFormatPalette.BuildEncodings("shift-jis", name => () => selected = name);

        Assert.Equal("いま", items.Single(i => i.Title == "Shift_JIS").Badge);
        items.Single(i => i.Title == "UTF-8（BOM 付き）").Execute();
        Assert.Equal("utf-8-bom", selected);
    }

    [Theory]
    [InlineData("設定ファイル", "shift-jis", null)]
    [InlineData("emoji 😀", "shift-jis", "😀")]          // Shift_JIS に無い文字：エディタは「?」にして黙って書くので止める
    [InlineData("café", "ascii", "é")]
    [InlineData("何でも 😀", "utf-8", null)]             // UTF-8 はどの文字も表せる
    public void Unrepresentable_characters_are_found_before_switching(string text, string encoding, string? lost)
        => Assert.Equal(lost, EditorFileFormatPalette.FirstUnrepresentable(text, encoding));

    // ===== パレット =====

    [Fact]
    public void Palette_lists_summary_then_findings_with_targets()
    {
        var report = EncodingInspector.Inspect(Utf8NoBom.GetBytes("a\nb\u3000c\n"));
        PaletteTarget? jumped = null;

        var items = EncodingInspectionPalette.Build(report, @"C:\w\a.txt", hasUnsavedChanges: true,
            target => () => jumped = target);

        Assert.Equal("UTF-8", items[0].Title);
        Assert.Equal("BOM なし", items[0].Badge);
        Assert.Contains("ディスク上の内容", items[0].Detail);   // 未保存の変更があることを断る
        Assert.Equal("LF", items[1].Title);
        var finding = items[2];
        Assert.Equal(new PaletteTarget(@"C:\w\a.txt", 2, 2), finding.Target);
        finding.Execute();
        Assert.Equal(finding.Target, jumped);
    }

    [Fact]
    public void Palette_says_so_when_nothing_found_or_undecodable()
    {
        var clean = EncodingInspectionPalette.Build(EncodingInspector.Inspect(Utf8NoBom.GetBytes("a\n")), "a.txt", false, _ => () => { });
        Assert.Contains(clean, i => i.Title.Contains("見つかりませんでした"));

        var binary = EncodingInspectionPalette.Build(EncodingInspector.Inspect([0x00, 0xFF]), "a.bin", false, _ => () => { });
        Assert.Equal("文字コードを判定できませんでした", Assert.Single(binary).Title);
    }
}
