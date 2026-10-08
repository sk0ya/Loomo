using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Buffer;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>編集に追従する位置・範囲（設計書 §35.3 Phase 0）。抜粋ペインは本文を比べ直さず、
/// エディタの差分（<see cref="TextBufferChange"/>）だけで抜粋の範囲を動かすので、ここがずれると
/// 抜粋が別の場所を指したまま編集を受け付けることになる。</summary>
public sealed class TextAnchorTests
{
    private static TextBufferChange Edit(int sl, int sc, int el, int ec, string text)
        => new(sl, sc, el, ec, text, Version: 1);

    [Fact]
    public void 前の変更では動かず後ろの変更では行と桁がずれる()
    {
        var point = new TextPoint(2, 4);

        Assert.Equal(point, TextAnchorMath.Transform(point, Edit(3, 0, 3, 0, "x\n"), AnchorBias.Before));
        Assert.Equal(new TextPoint(3, 4), TextAnchorMath.Transform(point, Edit(0, 0, 0, 0, "x\n"), AnchorBias.Before));
        Assert.Equal(new TextPoint(2, 6), TextAnchorMath.Transform(point, Edit(2, 1, 2, 1, "ab"), AnchorBias.Before));
        // 同じ行で前を行ごと消すと、残りの桁は消した位置の行へ寄る。
        Assert.Equal(new TextPoint(1, 5), TextAnchorMath.Transform(point, Edit(1, 3, 2, 2, ""), AnchorBias.Before));
    }

    [Fact]
    public void ちょうどの位置への挿入は寄せ方で前後が決まる()
    {
        var point = new TextPoint(1, 2);
        var insert = Edit(1, 2, 1, 2, "abc");

        Assert.Equal(point, TextAnchorMath.Transform(point, insert, AnchorBias.Before));
        Assert.Equal(new TextPoint(1, 5), TextAnchorMath.Transform(point, insert, AnchorBias.After));
    }

    [Fact]
    public void 消された範囲の中の位置は変更の端へ寄る()
    {
        var point = new TextPoint(1, 3);
        var replace = Edit(1, 1, 2, 0, "Z");

        Assert.Equal(new TextPoint(1, 1), TextAnchorMath.Transform(point, replace, AnchorBias.Before));
        Assert.Equal(new TextPoint(1, 2), TextAnchorMath.Transform(point, replace, AnchorBias.After));
    }

    [Fact]
    public void 抜粋の先頭と末尾で打った文字は抜粋に含まれる()
    {
        var range = new AnchoredRange(new TextPoint(2, 0), new TextPoint(4, 1));

        range.Apply(Edit(2, 0, 2, 0, "// "));
        range.Apply(Edit(4, 1, 4, 1, "\n    added();"));

        Assert.Equal(new TextPoint(2, 0), range.Start);
        Assert.Equal(new TextPoint(5, 12), range.End);
    }

    [Fact]
    public void 中身がまるごと消えると畳まれ再読み込みで無効になる()
    {
        var range = new AnchoredRange(new TextPoint(2, 0), new TextPoint(4, 1));
        range.Apply(Edit(1, 0, 5, 0, ""));
        Assert.True(range.IsCollapsed);

        var other = new AnchoredRange(new TextPoint(0, 0), new TextPoint(0, 1));
        other.Apply(new TextBufferChange(0, 0, 0, 1, "x", 2, TextBufferChangeKind.Reload));
        Assert.True(other.IsInvalidated);
    }

    [Fact]
    public void 重なる変更と接する変更だけを抜粋への変更とみなす()
    {
        var range = new AnchoredRange(new TextPoint(2, 0), new TextPoint(4, 1));

        Assert.True(range.Touches(Edit(3, 0, 3, 0, "x")));
        Assert.True(range.Touches(Edit(4, 1, 4, 1, "x")));
        Assert.True(range.Touches(Edit(1, 0, 2, 0, "")));
        Assert.False(range.Touches(Edit(0, 0, 1, 3, "x")));
        Assert.False(range.Touches(Edit(5, 0, 5, 0, "x")));
    }

    /// <summary>実際のバッファへランダムに編集を入れ、追従させた範囲の中身が「印を付けた文字列」のまま
    /// 残ることを確かめる（範囲の外だけを編集した場合）。</summary>
    [Fact]
    public void 範囲の外への編集では範囲の中身が変わらない()
    {
        var random = new Random(35);
        var buffer = new TextBuffer("head\n<<inside\nstill inside>>\ntail");
        var range = new AnchoredRange(new TextPoint(1, 0), new TextPoint(2, 14));
        buffer.Changed += range.Apply;
        var expected = Slice(buffer.GetText(), range);

        for (var i = 0; i < 500; i++)
        {
            var before = random.Next(2) == 0;
            if (before)
            {
                // 先頭ちょうどへの挿入は抜粋に含める仕様なので、ここでは範囲より前の行だけに入れる。
                var line = random.Next(0, range.Start.Line);
                var column = random.Next(0, buffer.GetLineLength(line) + 1);
                buffer.InsertText(line, column, random.Next(3) == 0 ? "a\nb" : "c");
            }
            else
            {
                var line = random.Next(range.End.Line, buffer.LineCount);
                var column = line == range.End.Line
                    ? range.End.Column + 1 + random.Next(0, Math.Max(1, buffer.GetLineLength(line) - range.End.Column))
                    : random.Next(0, buffer.GetLineLength(line) + 1);
                if (line == range.End.Line && column > buffer.GetLineLength(line)) continue;
                buffer.InsertText(line, column, random.Next(3) == 0 ? "d\ne" : "f");
            }
            Assert.Equal(expected, Slice(buffer.GetText(), range));
        }
    }

    private static string Slice(string text, AnchoredRange range)
    {
        var lines = text.Split('\n');
        if (range.Start.Line == range.End.Line)
            return lines[range.Start.Line][range.Start.Column..range.End.Column];
        var parts = new List<string> { lines[range.Start.Line][range.Start.Column..] };
        parts.AddRange(lines.Skip(range.Start.Line + 1).Take(range.End.Line - range.Start.Line - 1));
        parts.Add(lines[range.End.Line][..range.End.Column]);
        return string.Join("\n", parts);
    }
}
