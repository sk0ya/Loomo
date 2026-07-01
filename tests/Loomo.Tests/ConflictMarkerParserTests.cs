using System;
using System.Linq;
using sk0ya.Loomo.Core.Diff;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// コンフリクトマーカー解析（<see cref="ConflictMarkerParser"/>）の検証。Diff ペインのコンフリクト解消
/// UI がリージョン単位で ours/theirs/both を採用するための土台。
/// </summary>
public class ConflictMarkerParserTests
{
    [Fact]
    public void マーカーの無いファイルはコンフリクトなしと判定される()
    {
        var parsed = ConflictMarkerParser.Parse("line1\nline2\nline3\n");

        Assert.False(parsed.HasConflicts);
        var region = Assert.Single(parsed.Regions);
        Assert.Equal(ConflictRegionKind.Ordinary, region.Kind);
        Assert.Equal(new[] { "line1", "line2", "line3" }, region.Lines);
    }

    [Fact]
    public void twoway形式でoursとtheirsに分割される()
    {
        var text = "before\n<<<<<<< HEAD\nours-line\n=======\ntheirs-line\n>>>>>>> feature\nafter\n";

        var parsed = ConflictMarkerParser.Parse(text);

        Assert.True(parsed.HasConflicts);
        Assert.Equal(3, parsed.Regions.Count);
        Assert.Equal(new[] { "before" }, parsed.Regions[0].Lines);
        var conflict = parsed.Regions[1];
        Assert.Equal(ConflictRegionKind.Conflict, conflict.Kind);
        Assert.Equal("HEAD", conflict.OursLabel);
        Assert.Equal("feature", conflict.TheirsLabel);
        Assert.Equal(new[] { "ours-line" }, conflict.OursLines);
        Assert.Equal(new[] { "theirs-line" }, conflict.TheirsLines);
        Assert.Null(conflict.BaseLines);
        Assert.Equal(new[] { "after" }, parsed.Regions[2].Lines);
    }

    [Fact]
    public void diff3形式のbaseセクションを取得できる()
    {
        var text = "<<<<<<< HEAD\nours-line\n||||||| base-commit\nbase-line\n=======\ntheirs-line\n>>>>>>> feature\n";

        var parsed = ConflictMarkerParser.Parse(text);

        var conflict = Assert.Single(parsed.Regions);
        Assert.Equal(ConflictRegionKind.Conflict, conflict.Kind);
        Assert.NotNull(conflict.BaseLines);
        Assert.Equal(new[] { "base-line" }, conflict.BaseLines);
    }

    [Fact]
    public void 複数リージョンが通常行と混在しても分割できる()
    {
        var text =
            "top\n" +
            "<<<<<<< HEAD\nA-ours\n=======\nA-theirs\n>>>>>>> feature\n" +
            "middle\n" +
            "<<<<<<< HEAD\nB-ours\n=======\nB-theirs\n>>>>>>> feature\n" +
            "bottom\n";

        var parsed = ConflictMarkerParser.Parse(text);

        Assert.Equal(5, parsed.Regions.Count);
        Assert.Equal(new[]
        {
            ConflictRegionKind.Ordinary, ConflictRegionKind.Conflict, ConflictRegionKind.Ordinary,
            ConflictRegionKind.Conflict, ConflictRegionKind.Ordinary,
        }, parsed.Regions.Select(r => r.Kind));
        Assert.Equal(new[] { "middle" }, parsed.Regions[2].Lines);
    }

    [Theory]
    [InlineData(ConflictResolution.Ours, "before\nours-line\nafter\n")]
    [InlineData(ConflictResolution.Theirs, "before\ntheirs-line\nafter\n")]
    [InlineData(ConflictResolution.Both, "before\nours-line\ntheirs-line\nafter\n")]
    public void リージョン単位でours_theirs_bothを採用できる(ConflictResolution resolution, string expected)
    {
        var text = "before\n<<<<<<< HEAD\nours-line\n=======\ntheirs-line\n>>>>>>> feature\nafter\n";
        var parsed = ConflictMarkerParser.Parse(text);

        var resolved = ConflictMarkerParser.ResolveRegion(parsed, regionIndex: 1, resolution);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void 各リージョンの生ファイル内での開始行番号が正しい()
    {
        // 1:before 2:<<<<<<< 3:ours-line 4:======= 5:theirs-line 6:>>>>>>> 7:after
        var text = "before\n<<<<<<< HEAD\nours-line\n=======\ntheirs-line\n>>>>>>> feature\nafter\n";

        var parsed = ConflictMarkerParser.Parse(text);

        Assert.Equal(1, parsed.Regions[0].StartLine); // "before"
        Assert.Equal(3, parsed.Regions[1].StartLine); // ours-line の行
        Assert.Equal(7, parsed.Regions[2].StartLine); // "after"
    }

    [Fact]
    public void Result欄に書いた行でそのまま解決できる()
    {
        var text = "before\n<<<<<<< HEAD\nours-1\nours-2\n=======\ntheirs-1\ntheirs-2\n>>>>>>> feature\nafter\n";
        var parsed = ConflictMarkerParser.Parse(text);

        var resolved = ConflictMarkerParser.ResolveRegionWithLines(
            parsed, regionIndex: 1, lines: new[] { "custom-line" });

        Assert.Equal("before\ncustom-line\nafter\n", resolved);
    }

    [Fact]
    public void Result欄を空にすると両側とも削除される()
    {
        var text = "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> feature\nafter\n";
        var parsed = ConflictMarkerParser.Parse(text);

        var resolved = ConflictMarkerParser.ResolveRegionWithLines(
            parsed, regionIndex: 0, lines: Array.Empty<string>());

        Assert.Equal("after\n", resolved);
    }

    [Fact]
    public void 一方のコンフリクトだけ解決すれば他方のマーカーは残る()
    {
        var text =
            "<<<<<<< HEAD\nA-ours\n=======\nA-theirs\n>>>>>>> feature\n" +
            "middle\n" +
            "<<<<<<< HEAD\nB-ours\n=======\nB-theirs\n>>>>>>> feature\n";
        var parsed = ConflictMarkerParser.Parse(text);

        var resolved = ConflictMarkerParser.ResolveRegion(parsed, regionIndex: 0, ConflictResolution.Ours);

        Assert.Equal(
            "A-ours\nmiddle\n<<<<<<< HEAD\nB-ours\n=======\nB-theirs\n>>>>>>> feature\n", resolved);
        Assert.True(ConflictMarkerParser.Parse(resolved).HasConflicts);
    }

    [Fact]
    public void 末尾改行の有無を保って再構築する()
    {
        var withNewline = "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> feature\n";
        var withoutNewline = "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> feature";

        Assert.Equal("ours\n", ConflictMarkerParser.ResolveRegion(
            ConflictMarkerParser.Parse(withNewline), 0, ConflictResolution.Ours));
        Assert.Equal("ours", ConflictMarkerParser.ResolveRegion(
            ConflictMarkerParser.Parse(withoutNewline), 0, ConflictResolution.Ours));
    }
}
