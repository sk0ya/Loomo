using System;
using System.Collections.Generic;
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
