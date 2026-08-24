using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace sk0ya.Loomo.Services;

/// <summary>
/// コミット一覧の絞り込み式。フリーテキストを <c>field:value</c> トークンへ分解し、各トークンを
/// AND 結合した述語にする。接頭辞なしのトークンは全項目（件名・作者・ハッシュ・ref）へ部分一致。
/// git は再実行せず、読み込み済みの <see cref="GitLogRow"/> をクライアント側で判定する用途。
/// </summary>
/// <remarks>
/// 対応する接頭辞（別名を含む）：
/// <list type="bullet">
/// <item><c>author:</c> / <c>an:</c> — 作者名に部分一致。</item>
/// <item><c>msg:</c> / <c>message:</c> / <c>subject:</c> / <c>s:</c> — 件名に部分一致。</item>
/// <item><c>hash:</c> / <c>sha:</c> / <c>commit:</c> — ハッシュ（短縮・完全）に部分一致。</item>
/// <item><c>ref:</c> / <c>branch:</c> / <c>tag:</c> — refs（ブランチ・タグ）に部分一致。</item>
/// <item><c>date:</c> — 日付比較（<c>&gt;</c>/<c>&gt;=</c>/<c>&lt;</c>/<c>&lt;=</c>、範囲 <c>A..B</c>、前方一致）。</item>
/// </list>
/// 日付は <see cref="GitLogRow.Date"/> の先頭 10 文字（<c>yyyy-MM-dd</c>）を辞書順で比較する（日単位）。
/// 空白を含む値は <c>msg:"foo bar"</c> のように二重引用符で囲める。
/// </remarks>
public sealed class CommitLogFilter
{
    private readonly IReadOnlyList<Term> _terms;

    private CommitLogFilter(IReadOnlyList<Term> terms) => _terms = terms;

    /// <summary>有効なトークンが1つも無い（＝全件通す）か。</summary>
    public bool IsEmpty => _terms.Count == 0;

    public static CommitLogFilter Parse(string? text)
    {
        var terms = new List<Term>();
        foreach (var token in Tokenize(text))
            if (Term.From(token) is { } term)
                terms.Add(term);
        return new CommitLogFilter(terms);
    }

    /// <summary>
    /// コミット行が全トークン（AND）に合致するか。トークンが無ければ true。
    /// グラフ継続行（<see cref="GitLogRow.IsCommit"/> = false）の扱いは呼び出し側の責務。
    /// </summary>
    public bool Matches(GitLogRow row)
    {
        foreach (var term in _terms)
            if (!term.Matches(row))
                return false;
        return true;
    }

    /// <summary>日付比較に使う <c>yyyy-MM-dd</c> 部分。日付が無い／短い行は null。</summary>
    public static string? DayOf(GitLogRow row) =>
        row.Date is { Length: >= 10 } d ? d[..10] : null;

    /// <summary>
    /// この式のうち <b>git へ押し下げられる分</b>を取り出す（純粋）。押し下げた条件は
    /// <c>git log --author/--grep/--since/--until</c> として飛び、<b>読み込み済みページの外にある
    /// 古いコミットも検索対象になる</b>。押し下げた後もこの式自体はクライアント側で
    /// そのまま評価されるので、二重に効いても結果は変わらない（AND の重複）。
    ///
    /// <para>押し下げないもの：<c>hash:</c> と <c>ref:</c>（git に対応する絞り込みが無い）と、
    /// <b>16進数に見える素の検索語</b>（ハッシュ前方一致で1件を手繰る使い方を、本文検索に化けさせて
    /// 「見つからない」にしないため）。その他の素の検索語は<b>本文検索</b>として押し下げる
    /// ——git は作者と本文を AND で見るので「どちらでもいいから一致」は表現できず、
    /// 実際の用途のほとんどが件名探しであるため（作者は <c>author:</c> か作者ドロップダウンで指定する）。</para>
    /// </summary>
    public GitLogQuery ApplyTo(GitLogQuery query)
    {
        var authors = new List<string>();
        var messages = new List<string>();
        DateOnly? since = null;
        DateOnly? until = null;

        foreach (var term in _terms)
        {
            switch (term.Field)
            {
                case Field.Author:
                    authors.Add(term.Value);
                    break;
                case Field.Message:
                    messages.Add(term.Value);
                    break;
                case Field.Any when !LooksLikeHash(term.Value):
                    messages.Add(term.Value);
                    break;
                case Field.Date:
                    var (from, to) = term.DateBounds;
                    since = Later(since, from);
                    until = Earlier(until, to);
                    break;
            }
        }

        return query with
        {
            Authors = Combine(query.Authors, authors),
            Messages = Combine(query.Messages, messages),
            Since = Later(query.Since, since),
            Until = Earlier(query.Until, until),
        };
    }

    /// <summary>
    /// コミットハッシュの前方一致とみなすか（16進数だけの4文字以上、<b>かつ数字を含む</b>）。
    /// 数字を要求しないと <c>added</c> / <c>dead</c> / <c>feed</c> / <c>face</c> のような
    /// a-f だけの<b>ふつうの単語</b>までハッシュ扱いになり、押し下げられず
    /// 「読み込み済みのページしか探せない」に黙って戻ってしまう。
    /// 数字を含まない短縮ハッシュを打った場合は本文検索として扱われるが、それは
    /// <c>hash:</c> を付ければ済むし、単語を取りこぼすより実害が小さい。
    /// </summary>
    internal static bool LooksLikeHash(string value) =>
        value.Length >= 4 && value.All(char.IsAsciiHexDigit) && value.Any(char.IsAsciiDigit);

    private static IReadOnlyList<string> Combine(IReadOnlyList<string> existing, List<string> added)
    {
        if (added.Count == 0) return existing;
        if (existing.Count == 0) return added;
        var all = new List<string>(existing);
        all.AddRange(added);
        return all;
    }

    /// <summary>より狭い（新しい）下限を採る。</summary>
    private static DateOnly? Later(DateOnly? a, DateOnly? b) =>
        a is null ? b : b is null ? a : a.Value > b.Value ? a : b;

    /// <summary>より狭い（古い）上限を採る。</summary>
    private static DateOnly? Earlier(DateOnly? a, DateOnly? b) =>
        a is null ? b : b is null ? a : a.Value < b.Value ? a : b;

    /// <summary>二重引用符で空白をエスケープしつつ空白区切りでトークン化する。</summary>
    private static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in text)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private enum Field { Any, Author, Message, Hash, Ref, Date }

    /// <summary>1トークン（1条件）。日付トークンだけは <see cref="_date"/> に比較器を持つ。</summary>
    private sealed class Term
    {
        private readonly Field _field;
        private readonly string _value;
        private readonly DatePredicate? _date;

        private Term(Field field, string value, DatePredicate? date)
        {
            _field = field;
            _value = value;
            _date = date;
        }

        /// <summary>この条件が見る項目（git への押し下げ判断に使う）。</summary>
        public Field Field => _field;

        /// <summary>検索語そのもの（<c>field:</c> を剥がした後）。</summary>
        public string Value => _value;

        /// <summary>日付条件を「この日以降／この日まで」に均したもの。日付条件でなければ両方 null。</summary>
        public (DateOnly? From, DateOnly? To) DateBounds => _date?.Bounds ?? (null, null);

        /// <summary>トークン文字列を条件へ。空値の接頭辞付きトークン（例: <c>author:</c>）は null（無視）。</summary>
        public static Term? From(string token)
        {
            var colon = token.IndexOf(':');
            if (colon > 0 && FieldOf(token[..colon]) is { } field)
            {
                var value = token[(colon + 1)..];
                if (value.Length == 0) return null;
                if (field == Field.Date)
                    return DatePredicate.TryParse(value) is { } d ? new Term(field, value, d) : null;
                return new Term(field, value, null);
            }
            return token.Length == 0 ? null : new Term(Field.Any, token, null);
        }

        private static Field? FieldOf(string key) => key.ToLowerInvariant() switch
        {
            "author" or "an" => Field.Author,
            "msg" or "message" or "subject" or "s" => Field.Message,
            "hash" or "sha" or "commit" => Field.Hash,
            "ref" or "branch" or "tag" => Field.Ref,
            "date" or "d" => Field.Date,
            _ => null,
        };

        public bool Matches(GitLogRow row) => _field switch
        {
            Field.Author => Contains(row.Author, _value),
            Field.Message => Contains(row.Subject, _value),
            Field.Hash => Contains(row.Hash, _value) || Contains(row.ShortHash, _value),
            Field.Ref => Contains(row.Refs, _value),
            Field.Date => _date!.Matches(DayOf(row)),
            _ => Contains(row.Subject, _value)
                 || Contains(row.Author, _value)
                 || Contains(row.ShortHash, _value)
                 || Contains(row.Hash, _value)
                 || Contains(row.Refs, _value),
        };

        private static bool Contains(string? haystack, string term) =>
            haystack is not null && haystack.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>date:</c> の値を比較器にしたもの。日単位・辞書順比較（<c>yyyy-MM-dd</c> はそれで正しく並ぶ）。</summary>
    private sealed class DatePredicate
    {
        private enum Op { Prefix, After, AfterOrEqual, Before, BeforeOrEqual, Range }

        private readonly Op _op;
        private readonly string _a;
        private readonly string _b;

        private DatePredicate(Op op, string a, string b = "")
        {
            _op = op;
            _a = a;
            _b = b;
        }

        public static DatePredicate? TryParse(string value)
        {
            var range = value.IndexOf("..", StringComparison.Ordinal);
            if (range >= 0)
            {
                var from = value[..range];
                var to = value[(range + 2)..];
                if (from.Length == 0 && to.Length == 0) return null;
                return new DatePredicate(Op.Range, from, to);
            }
            if (value.StartsWith(">=", StringComparison.Ordinal)) return Make(Op.AfterOrEqual, value[2..]);
            if (value.StartsWith("<=", StringComparison.Ordinal)) return Make(Op.BeforeOrEqual, value[2..]);
            if (value.StartsWith(">", StringComparison.Ordinal)) return Make(Op.After, value[1..]);
            if (value.StartsWith("<", StringComparison.Ordinal)) return Make(Op.Before, value[1..]);
            return Make(Op.Prefix, value);
        }

        private static DatePredicate? Make(Op op, string operand) =>
            operand.Length == 0 ? null : new DatePredicate(op, operand);

        /// <summary>
        /// git へ渡すための「この日以降／この日まで」。<c>2026-08</c> のような部分指定はその月の
        /// 初日・末日に均す。解釈できない値は両方 null＝押し下げない（クライアント側の判定は残る）。
        /// </summary>
        public (DateOnly? From, DateOnly? To) Bounds
        {
            get
            {
                var (start, end) = SpanOf(_a);
                return _op switch
                {
                    Op.Prefix => (start, end),
                    // 「その範囲より後」ではなく<b>範囲の先頭</b>を下限にする。クライアント側の判定は
                    // 文字列比較なので date:>2026-08 は 2026-08-01 以降を通す（"2026-08-01" > "2026-08"）。
                    // ここで月末の翌日を渡すと git 側だけが8月を丸ごと落とし、篩が逆転する。
                    Op.After => (start, null),
                    Op.AfterOrEqual => (start, null),
                    Op.Before => (null, start?.AddDays(-1)),
                    Op.BeforeOrEqual => (null, end),
                    Op.Range => (SpanOf(_a).Start, SpanOf(_b).End),
                    _ => (null, null),
                };
            }
        }

        /// <summary><c>yyyy</c> / <c>yyyy-MM</c> / <c>yyyy-MM-dd</c> をその範囲の初日・末日にする。</summary>
        private static (DateOnly? Start, DateOnly? End) SpanOf(string value)
        {
            if (value.Length == 0) return (null, null);
            if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day))
                return (day, day);
            if (DateOnly.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var month))
                return (month, month.AddMonths(1).AddDays(-1));
            if (value.Length == 4 && int.TryParse(value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var year) && year is >= 1 and <= 9999)
                return (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
            return (null, null);
        }

        public bool Matches(string? day)
        {
            if (day is null) return false;
            return _op switch
            {
                Op.Prefix => day.StartsWith(_a, StringComparison.Ordinal),
                Op.After => string.CompareOrdinal(day, _a) > 0,
                Op.AfterOrEqual => string.CompareOrdinal(day, _a) >= 0,
                Op.Before => string.CompareOrdinal(day, _a) < 0,
                Op.BeforeOrEqual => string.CompareOrdinal(day, _a) <= 0,
                Op.Range => (_a.Length == 0 || string.CompareOrdinal(day, _a) >= 0)
                            && (_b.Length == 0 || string.CompareOrdinal(day, _b) <= 0),
                _ => false,
            };
        }
    }
}
