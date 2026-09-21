using System.Collections.Generic;
using System.Linq;
using sk0ya.Loomo.App.Input;

namespace sk0ya.Loomo.App.Services;

/// <summary>キーキャプチャ中の押下列、表示文、確定値を管理する。</summary>
internal sealed class KeySequenceCaptureState
{
    private readonly List<KeyChord> _chords = new();

    public bool IsComplete => _chords.Count >= KeySequence.MaxChords;

    public string PromptText => _chords.Count == 0
        ? "キーを押す…"
        : string.Join(" ", _chords.Select(chord => chord.Format())) + " …";

    public void Add(KeyChord chord)
    {
        if (!IsComplete)
            _chords.Add(chord);
    }

    public KeySequence? ToSequence()
        => _chords.Count == 0 ? null : new KeySequence(_chords.ToArray());

    public void Clear() => _chords.Clear();
}
