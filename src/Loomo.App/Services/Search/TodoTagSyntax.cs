namespace sk0ya.Loomo.App.Services;

/// <summary>TODO として拾う書き方。文章中の「TODO リスト」などを拾わないよう、タグは行頭
/// （コメント記号・箇条書き・チェックボックス・引用の直後）に置かれ、コロンが続くものに限る。
/// 例: <c>// TODO: 直す</c>・<c># FIXME(koya): 確認</c>・<c>- TODO: 書く</c>・<c>- [ ] NOTE: 残す</c>。</summary>
public static class TodoTagSyntax
{
    // タグの名前と、担当者・コロン。ripgrep（候補の絞り込み）と .NET（判定）の両方で同じ部品を使う。
    private const string TagNames = "TODO|FIXME|HACK|NOTE";
    private const string AfterTag = @"(?:\([^)\r\n]*\))?[ \t]*[:：]";

    /// <summary>ripgrep で候補行を絞るための広めの式（行頭の判定は <see cref="Find"/> で行う）。</summary>
    public const string CandidatePattern = @"\b(?:" + TagNames + ")" + AfterTag;

    private static readonly Regex Tag = new(@"\b(?<tag>" + TagNames + ")" + AfterTag, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TagHere = new(@"\G(?<tag>" + TagNames + ")" + AfterTag, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // タグの前に置いてよいもの：空白・コメント記号・引用・箇条書き・チェックボックスだけ。
    private static readonly Regex Head = new(
        @"^\s*(?:(?://+!?|/\*+!?|\*+|#+|--+|<!--|<#|;+|'+|%+)\s*)?(?:>\s*)*(?:(?:[-*+]|\d+[.)])\s+)?(?:\[[ xX]\]\s+)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // Markdown のコードフェンス（``` / ~~~）。中の見本のコードを TODO として拾わないために数える。
    private static readonly Regex Fence = new(@"^ {0,3}(?:`{3,}|~{3,})", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>行の <paramref name="headStart"/> 以降（コードならコメントの開始位置）の先頭にあるタグを返す。</summary>
    public static Match? Find(string line, int headStart = 0, int? end = null)
    {
        var limit = Math.Min(end ?? line.Length, line.Length);
        for (var match = Tag.Match(line, headStart); match.Success && match.Index + match.Length <= limit; match = match.NextMatch())
            if (Head.IsMatch(line.AsSpan(headStart, match.Index - headStart))) return match;
        return null;
    }

    /// <summary>行の <paramref name="index"/>（0 始まり）にあるタグ・担当者・コロンの長さ。タグでなければ 0。</summary>
    public static int LengthAt(string line, int index)
    {
        if (index < 0 || index >= line.Length) return 0;
        var match = TagHere.Match(line, index);
        return match.Success ? match.Length : 0;
    }

    /// <summary>Markdown のコードフェンスの開き／閉じの行か。</summary>
    public static bool IsFence(string line) => Fence.IsMatch(line);

    /// <summary>行のどこかにタグの形があるか（無い行はコメントの判定まで進めない）。</summary>
    public static bool HasTag(string line) => Tag.IsMatch(line);
}
