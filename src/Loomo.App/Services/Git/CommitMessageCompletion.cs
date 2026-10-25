using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// コミットメッセージの補完の候補（§24.95）。純関数。差分で変えた行の識別子と、変えたファイルの名前を候補にする
/// ——差分の素材をメッセージへ運ぶ（Magit 4.6.0 の「modified definitions and symbols from modified lines」）。
/// </summary>
internal static partial class CommitMessageCompletion
{
    /// <summary>候補の上限。</summary>
    public const int MaxCandidates = 500;

    /// <summary>この長さより短い語は候補にしない（打った方が早い）。</summary>
    public const int MinLength = 3;

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();

    /// <summary>どの言語でも変更の中身を言わない語（キーワード・よくある型名）。</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "abstract", "and", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "class", "const",
        "continue", "def", "default", "del", "delegate", "do", "double", "elif", "else", "enum", "event", "except", "explicit",
        "export", "extends", "extern", "false", "finally", "fixed", "float", "for", "foreach", "from", "func", "function", "get",
        "global", "goto", "if", "implements", "implicit", "import", "in", "init", "int", "interface", "internal", "is", "lambda",
        "let", "lock", "long", "namespace", "new", "nil", "None", "not", "null", "object", "operator", "or", "out", "override",
        "package", "partial", "pass", "private", "protected", "public", "raise", "readonly", "record", "ref", "required", "return",
        "sealed", "self", "set", "short", "sizeof", "static", "string", "struct", "super", "switch", "this", "throw", "True",
        "False", "true", "try", "type", "typeof", "uint", "ulong", "unsafe", "ushort", "using", "value", "var", "virtual", "void",
        "volatile", "where", "while", "with", "yield", "nameof", "select", "throws", "final", "undefined", "number", "boolean",
    };

    /// <summary>
    /// 差分（<c>git diff -U0</c>）と変えたファイルから候補を作る。並びは「効きそうな順」：
    /// 変えたファイルの名前（拡張子なし・あり）、hunk の見出しに出る囲みの関数・型（git が付ける文脈）、変えた行に多く出た語の順。
    /// 足した行の語は消した行の語より重く数える（メッセージは「何をしたか」なので）。
    /// </summary>
    public static IReadOnlyList<string> Build(string diff, IEnumerable<string> paths)
    {
        var score = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string word, int weight)
        {
            if (word.Length < MinLength || Noise.Contains(word)) return;
            score[word] = score.TryGetValue(word, out var s) ? s + weight : weight;
        }

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.Replace('\\', '/'));
            if (name.Length == 0) continue;
            Add(name, 1000);
            Add(Path.GetFileNameWithoutExtension(name), 1000);
        }
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                // "@@ -10,0 +11,2 @@ public void Refresh()"：2 つ目の @@ の後ろが囲みの文脈。
                var close = line.IndexOf("@@", 2, StringComparison.Ordinal);
                if (close > 0)
                    foreach (Match m in Identifier().Matches(line[(close + 2)..]))
                        Add(m.Value, 20);
                continue;
            }
            var weight = line.StartsWith('+') ? 2 : line.StartsWith('-') ? 1 : 0;
            if (weight == 0) continue;
            foreach (Match m in Identifier().Matches(line, 1))
                Add(m.Value, weight);
        }
        return score
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>キャレットの直前の打ちかけの語（識別子とファイル名に使う文字の並び）。無ければ空。</summary>
    public static (int Start, string Prefix) WordBeforeCaret(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && IsWordChar(text[start - 1]))
            start--;
        return (start, text[start..caret]);
    }

    /// <summary>打ちかけの語に合う候補（大文字小文字を区別しない前方一致・打った語そのものは除く）。</summary>
    public static IReadOnlyList<string> Match(IReadOnlyList<string> candidates, string prefix, int max = 8)
    {
        if (prefix.Length < 2)
            return [];
        return candidates
            .Where(c => c.Length > prefix.Length && c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// キャレットの位置で出す候補と、置き換える範囲の始まり。ファイル名（<c>GitService.cs</c>）の途中でも合うよう、まず <c>.</c> や <c>-</c> を含めた語で探し、
    /// 合わなければ最後の <c>.</c>／<c>-</c> の後ろだけで探す（<c>vm.Refr</c> → <c>Refresh</c>）。
    /// </summary>
    public static (int Start, string Prefix, IReadOnlyList<string> Matches) Suggest(IReadOnlyList<string> candidates, string text, int caret)
    {
        var (start, prefix) = WordBeforeCaret(text, caret);
        var matches = Match(candidates, prefix);
        if (matches.Count > 0)
            return (start, prefix, matches);
        var cut = prefix.LastIndexOfAny(['.', '-']);
        if (cut < 0)
            return (start, prefix, matches);
        var tail = prefix[(cut + 1)..];
        return (start + cut + 1, tail, Match(candidates, tail));
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-';
}
