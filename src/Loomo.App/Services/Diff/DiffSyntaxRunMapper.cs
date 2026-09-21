using Editor.Core.Syntax;

namespace sk0ya.Loomo.App.Services;

internal readonly record struct DiffSyntaxRun(int Start, int End, object? ForegroundKey);

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
            var (start, end, key) = segments[index];
            while (index + 1 < segments.Count && ReferenceEquals(segments[index + 1].ForegroundKey, key))
                end = segments[++index].End;
            merged.Add(new DiffSyntaxRun(start, end, key));
        }
        return merged;
    }
}
