using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// Claude へ次の頼みごとに添える一言の待ち行列（§45.6 P3・§45.4 P1 の受け口）。セッションごとに積み、
/// そのセッションの次の <c>UserPromptSubmit</c> で添える。
/// <para><b>重複を許す（届かなかった場合の保険）</b>：フックの返事が橋へ届いたかは Loomo からは確かめられない
/// （橋が 3 秒で諦める・落ちることがある）。そこで 1 件は <see cref="MaxOffers"/> 回まで添え、そのあと消える。
/// 1 回目が届かなくても次の頼みごとで 2 回目が届く。届いていれば 2 回目は重複になるが、Claude には害がない。</para>
/// <para>積むのは Loomo の中の出来事（動きが悪い方へ変わった・人間が書いた指摘）。Claude の画面には何も打たない——
/// フックの返事の <c>additionalContext</c> として、Claude が頼みごとを読む前に文脈へ入るだけ。</para>
/// <para>溜まりすぎない：セッションごとに <see cref="MaxPerSession"/> 件まで、古いものから捨てる。
/// スレッドセーフ（フックの橋と Loomo の UI が別スレッドから触る）。</para>
/// </summary>
public sealed class ClaudeNoteQueue
{
    public const int MaxPerSession = 8;

    /// <summary>1 件を添える回数の上限（1 回目が届かなかった場合の保険に 2 回）。</summary>
    public const int MaxOffers = 2;

    private sealed class Entry
    {
        public Entry(ClaudeNote note) => Note = note;
        public ClaudeNote Note { get; }
        public int Offers { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _bySession = new(StringComparer.Ordinal);

    /// <summary>積む。セッション ID が空なら何もしない（どの Claude に届けるか分からないものは持たない）。</summary>
    public void Add(string sessionId, ClaudeNote note)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(note.Text))
            return;
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var list))
                _bySession[sessionId] = list = [];
            list.Add(new Entry(note));
            // 上限を超えたら、まず「動き」の知らせから古い順に捨てる。人間が書いた「指摘」は、動きが無くなるまで残す。
            while (list.Count > MaxPerSession)
            {
                var victim = list.FindIndex(e => e.Note.Kind != ClaudeNote.KindReview);
                list.RemoveAt(victim >= 0 ? victim : 0);
            }
        }
    }

    /// <summary>このセッションの次の頼みごとに添えるものを取り出す。各知らせは <see cref="MaxOffers"/> 回まで返り、
    /// その回数を使い切ったものは消える。無ければ空。</summary>
    public IReadOnlyList<ClaudeNote> Take(string sessionId)
    {
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var list))
                return [];
            var offered = new List<ClaudeNote>(list.Count);
            foreach (var entry in list)
            {
                entry.Offers++;
                offered.Add(entry.Note);
            }
            list.RemoveAll(e => e.Offers >= MaxOffers);
            if (list.Count == 0)
                _bySession.Remove(sessionId);
            return offered;
        }
    }

    /// <summary>ファイルの場所に宛てた指摘（エディタの印に出すもの）。どのセッションのものでも、取らずに見る。</summary>
    public IReadOnlyList<ClaudeNote> ReviewNotesFor(string path)
    {
        lock (_gate)
        {
            return _bySession.Values.SelectMany(list => list).Select(e => e.Note)
                .Where(note => note.Kind == ClaudeNote.KindReview && note.Path is { Length: > 0 }
                               && string.Equals(note.Path, path, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <summary>取らずに見る（一覧に出す）。取ったものは消えない。</summary>
    public IReadOnlyList<ClaudeNote> Peek(string sessionId)
    {
        lock (_gate)
            return _bySession.TryGetValue(sessionId, out var list) ? list.Select(e => e.Note).ToList() : [];
    }

    /// <summary>溜まっているセッションの一覧（一覧画面用）。</summary>
    public IReadOnlyList<string> Sessions()
    {
        lock (_gate)
            return _bySession.Keys.ToList();
    }

    /// <summary>この知らせを取り消す（値で一致するものを外す）。外れたら true。</summary>
    public bool Discard(string sessionId, ClaudeNote note)
    {
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var list))
                return false;
            var index = list.FindIndex(e => e.Note.Equals(note));
            if (index < 0)
                return false;
            list.RemoveAt(index);
            if (list.Count == 0)
                _bySession.Remove(sessionId);
            return true;
        }
    }

    /// <summary>いま溜まっている件数（テストと、見出しに出す数）。</summary>
    public int Count(string sessionId)
    {
        lock (_gate)
            return _bySession.TryGetValue(sessionId, out var list) ? list.Count : 0;
    }

    /// <summary>フックの <c>additionalContext</c> に入れる本文。取り出した順に、見出しを付けて並べる。</summary>
    public static string FormatContext(IReadOnlyList<ClaudeNote> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Loomo（この部屋）からの知らせです。頼みごとに関係するなら読んでください：");
        foreach (var note in notes)
            sb.Append("- ").AppendLine(note.Text.Trim());
        return sb.ToString().TrimEnd();
    }
}

/// <summary>1 件の知らせ。<see cref="Kind"/> は何の知らせか（読み手＝Claude が、どれを重く見るかを決める）。</summary>
public sealed record ClaudeNote(string Kind, string Text, DateTimeOffset At, string? Path = null, int Line = 0)
{
    public const string KindMotionWorsened = "動き";
    public const string KindReview = "指摘";
}
