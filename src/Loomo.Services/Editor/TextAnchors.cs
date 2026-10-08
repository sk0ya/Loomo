using System;
using Editor.Core.Buffer;

namespace sk0ya.Loomo.Services;

/// <summary>文書中の位置（0 始まりの行・桁。桁は UTF-16 単位で、本文は "\n" 区切り）。</summary>
public readonly record struct TextPoint(int Line, int Column) : IComparable<TextPoint>
{
    public int CompareTo(TextPoint other)
        => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    public static bool operator <(TextPoint a, TextPoint b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPoint a, TextPoint b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPoint a, TextPoint b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPoint a, TextPoint b) => a.CompareTo(b) >= 0;
}

/// <summary>ちょうどその位置への挿入があったとき、位置が挿入の前に残るか（<see cref="Before"/>）、
/// 挿入の後ろへ押し出されるか（<see cref="After"/>）。</summary>
public enum AnchorBias
{
    Before,
    After,
}

/// <summary>
/// 編集に追従する位置の計算（設計書 §35.3 Phase 0）。<see cref="TextBufferChange"/>（変更前の座標の範囲＋
/// 新しい文字列）を1つ受けて、変更前の位置を変更後の位置へ写す。本文を見ないので、キー入力ごとに
/// 呼んでも文書の大きさに比例するコストはかからない。
/// </summary>
public static class TextAnchorMath
{
    public static TextPoint Transform(TextPoint point, TextBufferChange change, AnchorBias bias)
    {
        var start = new TextPoint(change.StartLine, change.StartColumn);
        var end = new TextPoint(change.EndLine, change.EndColumn);
        if (point < start) return point;
        if (point == start && (start != end || bias == AnchorBias.Before)) return point;

        var inserted = InsertedEnd(start, change.NewText);
        if (point > end)
        {
            // 変更範囲より後ろ：同じ行なら桁ごと、別の行なら行だけずれる。
            return point.Line == end.Line
                ? new TextPoint(inserted.Line, inserted.Column + (point.Column - end.Column))
                : new TextPoint(point.Line + (inserted.Line - end.Line), point.Column);
        }

        // 置き換えられた範囲の中（終端ちょうどを含む）：消えた位置は、前寄せなら変更の始まり、
        // 後ろ寄せなら挿入した文字列の後ろへ寄せる。終端ちょうどは常に挿入の後ろ。
        if (point == end) return inserted;
        return bias == AnchorBias.Before ? start : inserted;
    }

    /// <summary><paramref name="start"/> に <paramref name="text"/> を入れたとき、その末尾が来る位置。</summary>
    public static TextPoint InsertedEnd(TextPoint start, string text)
    {
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0) return new TextPoint(start.Line, start.Column + text.Length);
        var breaks = 0;
        foreach (var ch in text)
            if (ch == '\n') breaks++;
        return new TextPoint(start.Line + breaks, text.Length - lastBreak - 1);
    }
}

/// <summary>
/// 編集に追従する範囲（抜粋の1つ・シンボルの範囲など）。始まりは前寄せ（始まりちょうどへの挿入は範囲に入る）。
/// 終わりの寄せ方は使い方で選ぶ：文字の範囲なら後ろ寄せ（末尾で打った文字が範囲からこぼれない）、
/// 行の半開区間（終わり＝次の行の行頭）なら前寄せ（直後の行への挿入は範囲の外）。
/// </summary>
public sealed class AnchoredRange(TextPoint start, TextPoint end, AnchorBias endBias = AnchorBias.After)
{
    public TextPoint Start { get; private set; } = start;
    public TextPoint End { get; private set; } = end;

    /// <summary>範囲の中身がまるごと消えて空になった（抜粋なら畳むか外す合図）。</summary>
    public bool IsCollapsed => Start == End;

    /// <summary>文書が読み直された（<see cref="TextBufferChangeKind.Reload"/>）。以後の位置は意味を持たないので、
    /// 持ち主がシンボルなどから引き直す。</summary>
    public bool IsInvalidated { get; private set; }

    /// <summary>この範囲と重なる（または境界で接する）変更か。抜粋の表示を作り直すかの判定に使う。</summary>
    public bool Touches(TextBufferChange change)
        => !(new TextPoint(change.EndLine, change.EndColumn) < Start)
           && !(new TextPoint(change.StartLine, change.StartColumn) > End);

    public void Apply(TextBufferChange change)
    {
        if (IsInvalidated) return;
        if (change.Kind == TextBufferChangeKind.Reload)
        {
            IsInvalidated = true;
            return;
        }
        Start = TextAnchorMath.Transform(Start, change, AnchorBias.Before);
        End = TextAnchorMath.Transform(End, change, endBias);
    }
}
