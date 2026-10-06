using Editor.Core.Syntax;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.Services;

/// <param name="Changed">行内差分で「変わった」範囲か（背景を濃く塗る）。</param>
internal readonly record struct DiffSyntaxRun(int Start, int End, object? ForegroundKey, bool Changed = false);

/// <summary>構文トークンを行テキストを保つ連続区間へまとめる。</summary>
internal static class DiffSyntaxRunMapper
{
    internal static IReadOnlyList<DiffSyntaxRun> Map(
        string text, IReadOnlyList<SyntaxToken> tokens, Func<TokenKind, object?> foregroundKey)
    {
        var segments = new List<DiffSyntaxRun>();
        var position = 0;
        foreach (var token in tokens)
        {
            // 重なり・逆順のトークンでも行を欠落・重複させない。
            var start = Math.Clamp(token.StartColumn, position, text.Length);
            var end = Math.Clamp(start + token.Length, start, text.Length);
            if (end == start) continue;
            if (start > position) segments.Add(new DiffSyntaxRun(position, start, null));
            segments.Add(new DiffSyntaxRun(start, end, foregroundKey(token.Kind)));
            position = end;
        }
        if (position < text.Length)
            segments.Add(new DiffSyntaxRun(position, text.Length, null));

        var merged = new List<DiffSyntaxRun>(segments.Count);
        for (var index = 0; index < segments.Count; index++)
        {
            var (start, end, key, _) = segments[index];
            while (index + 1 < segments.Count && ReferenceEquals(segments[index + 1].ForegroundKey, key))
                end = segments[++index].End;
            merged.Add(new DiffSyntaxRun(start, end, key));
        }
        return merged;
    }

    /// <summary>
    /// 区間の並び（行全体を隙間なく覆うもの）を、行内差分の範囲の境目でさらに割って
    /// <see cref="DiffSyntaxRun.Changed"/> を付ける。色（<see cref="DiffSyntaxRun.ForegroundKey"/>）は保つ。
    /// 範囲は昇順・重なり無しで来る前提（<see cref="InlineDiff"/> の出力）。行の外へはみ出す範囲は切り詰める。
    /// </summary>
    internal static IReadOnlyList<DiffSyntaxRun> SplitByChanges(
        IReadOnlyList<DiffSyntaxRun> runs, IReadOnlyList<TextSpan>? changes)
    {
        if (changes is not { Count: > 0 }) return runs;
        var result = new List<DiffSyntaxRun>(runs.Count + 2 * changes.Count);
        foreach (var run in runs)
        {
            var position = run.Start;
            foreach (var change in changes)
            {
                var start = Math.Max(change.Start, position);
                var end = Math.Min(change.End, run.End);
                if (end <= start) continue;
                if (start > position) result.Add(run with { Start = position, End = start, Changed = false });
                result.Add(run with { Start = start, End = end, Changed = true });
                position = end;
            }
            if (position < run.End) result.Add(run with { Start = position, End = run.End, Changed = false });
        }
        return result;
    }
}
