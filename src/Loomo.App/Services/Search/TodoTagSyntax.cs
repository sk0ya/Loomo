namespace sk0ya.Loomo.App.Services;

/// <summary>TODO として拾う書き方。文章中の「TODO リスト」などを拾わないよう、タグは行頭
/// （コメント記号・箇条書き・チェックボックス・引用の直後）に置かれ、コロンが続くものに限る。
/// 例: <c>// TODO: 直す</c>・<c># FIXME(koya): 確認</c>・<c>- TODO: 書く</c>・<c>- [ ] NOTE: 残す</c>。</summary>
public static class TodoTagSyntax
{
    /// <summary>ripgrep で候補行を絞るための広めの式（行頭の判定は <see cref="Find"/> で行う）。</summary>
    public const string CandidatePattern = @"\b(TODO|FIXME|HACK|NOTE)(\([^)]*\))?[ \t]*[:：]";

    private static readonly Regex Tag = new(@"\b(?<tag>TODO|FIXME|HACK|NOTE)(?:\([^)\r\n]*\))?[ \t]*[:：]",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // タグの前に置いてよいもの：空白・コメント記号・引用・箇条書き・チェックボックスだけ。
    private static readonly Regex Head = new(
        @"^\s*(?:(?://+!?|/\*+!?|\*+|#+|--+|<!--|<#|;+|'+|%+)\s*)?(?:>\s*)*(?:(?:[-*+]|\d+[.)])\s+)?(?:\[[ xX]\]\s+)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>行の <paramref name="headStart"/> 以降（コードならコメントの開始位置）の先頭にあるタグを返す。</summary>
    public static Match? Find(string line, int headStart = 0, int? end = null)
    {
        var limit = Math.Min(end ?? line.Length, line.Length);
        for (var match = Tag.Match(line, headStart); match.Success && match.Index + match.Length <= limit; match = match.NextMatch())
            if (Head.IsMatch(line.AsSpan(headStart, match.Index - headStart).ToString())) return match;
        return null;
    }

    /// <summary>行の <paramref name="index"/>（0 始まり）にあるタグ・担当者・コロンの長さ。タグでなければ 0。</summary>
    public static int LengthAt(string line, int index)
    {
        if (index < 0 || index >= line.Length) return 0;
        var match = Tag.Match(line, index);
        return match.Success && match.Index == index ? match.Length : 0;
    }
}
