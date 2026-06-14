using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.App.Input;

/// <summary>
/// 1〜2 個の <see cref="KeyChord"/> の並び。長さ 1 は単一ジェスチャ（例: <c>Ctrl+Shift+P</c>）、
/// 長さ 2 はプレフィックス連鎖（例: <c>Ctrl+W H</c> ＝ vim 風の 2 ストローク）。
/// 表記は chord を半角スペースで連結する。3 ストローク以上は扱わない。
/// </summary>
public sealed class KeySequence : IEquatable<KeySequence>
{
    public const int MaxChords = 2;

    public IReadOnlyList<KeyChord> Chords { get; }

    public KeySequence(IReadOnlyList<KeyChord> chords)
    {
        if (chords is null || chords.Count is < 1 or > MaxChords)
            throw new ArgumentException($"KeySequence は 1〜{MaxChords} chord。", nameof(chords));
        Chords = chords.ToArray();
    }

    public KeySequence(params KeyChord[] chords) : this((IReadOnlyList<KeyChord>)chords) { }

    public int Count => Chords.Count;

    /// <summary>連鎖の先頭 chord（プレフィックス判定に使う）。</summary>
    public KeyChord First => Chords[0];

    /// <summary>連鎖の末尾 chord（リサイズモードの反復キー導出などに使う）。</summary>
    public KeyChord Last => Chords[^1];

    public string Format() => string.Join(" ", Chords.Select(c => c.Format()));

    public override string ToString() => Format();

    /// <summary>"Ctrl+W H" のような表記をパースする。空・不正・長さ超過は null。</summary>
    public static KeySequence? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > MaxChords) return null;

        var chords = new List<KeyChord>(parts.Length);
        foreach (var part in parts)
        {
            if (KeyChord.TryParse(part) is not { } chord) return null;
            chords.Add(chord);
        }
        return new KeySequence(chords);
    }

    public bool Equals(KeySequence? other)
        => other is not null && Chords.SequenceEqual(other.Chords);

    public override bool Equals(object? obj) => Equals(obj as KeySequence);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var chord in Chords) hash.Add(chord);
        return hash.ToHashCode();
    }
}
