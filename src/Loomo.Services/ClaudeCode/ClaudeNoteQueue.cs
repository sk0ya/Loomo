using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.Services.ClaudeCode;

/// <summary>
/// Claude へ次の頼みごとに添える一言の待ち行列（§45.6 P3・§45.4 P1 の受け口）。セッションごとに積み、
/// そのセッションの次の <c>UserPromptSubmit</c> で一度だけ取り出す（取ったら消える）。
/// <para>積むのは Loomo の中の出来事（動きが悪い方へ変わった・人間が書いた指摘）。Claude の画面には何も打たない——
/// フックの返事の <c>additionalContext</c> として、Claude が頼みごとを読む前に文脈へ入るだけ。</para>
/// <para>溜まりすぎない：セッションごとに <see cref="MaxPerSession"/> 件まで、古いものから捨てる（取られないまま
/// 何十件も溜まると、次の頼みごとが読めなくなる）。スレッドセーフ（フックの橋と Loomo の UI が別スレッドから触る）。</para>
/// </summary>
public sealed class ClaudeNoteQueue
{
    public const int MaxPerSession = 8;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<ClaudeNote>> _bySession = new(StringComparer.Ordinal);

    /// <summary>積む。セッション ID が空なら何もしない（どの Claude に届けるか分からないものは持たない）。</summary>
    public void Add(string sessionId, ClaudeNote note)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(note.Text))
            return;
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var list))
                _bySession[sessionId] = list = [];
            list.Add(note);
            if (list.Count > MaxPerSession)
                list.RemoveRange(0, list.Count - MaxPerSession);
        }
    }

    /// <summary>このセッションの次の頼みごとに添えるものを取り出す（取ったら消える）。無ければ空。</summary>
    public IReadOnlyList<ClaudeNote> Take(string sessionId)
    {
        lock (_gate)
        {
            if (!_bySession.Remove(sessionId, out var list))
                return [];
            return list;
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
public sealed record ClaudeNote(string Kind, string Text, DateTimeOffset At)
{
    public const string KindMotionWorsened = "動き";
    public const string KindReview = "指摘";
}
