using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>TypeScript / JavaScript のテストソース走査（vitest / jest 用のテストエクスプローラの元データ）。
/// dotnet 側の <c>TestDiscoveryService</c>（属性の行ベース正規表現）と同じくビルドを伴わないベストエフォート：
/// <c>*.test.ts</c> / <c>*.spec.ts</c> 系のファイルから <c>describe(...)</c> / <c>it(...)</c> / <c>test(...)</c> の
/// 第 1 引数（文字列リテラル）を拾い、describe の入れ子は波かっこ深度の近似追跡で「a &gt; b &gt; タイトル」に組み立てる。
/// 深度は文字列/コメント内の波かっこも数える粗い近似（実害は稀で、外れてもタイトルの前置が崩れるだけ）。</summary>
public static partial class TsTestDiscovery
{
    /// <summary>発見した 1 テスト。<see cref="Title"/> は describe 連結済み（"a &gt; b &gt; テスト名"）。
    /// <see cref="Line1"/> は 1 始まり。<see cref="IsEach"/> は <c>it.each</c> 等のパラメータ化テスト。</summary>
    public sealed record TsDiscoveredTest(string FilePath, string Title, int Line1, bool IsEach);

    /// <summary>テストファイルと見なす拡張子パターン（vitest / jest の既定命名）。</summary>
    public static readonly string[] TestFilePatterns =
    [
        "*.test.ts", "*.spec.ts", "*.test.tsx", "*.spec.tsx",
        "*.test.mts", "*.spec.mts", "*.test.cts", "*.spec.cts",
        "*.test.js", "*.spec.js", "*.test.jsx", "*.spec.jsx",
        "*.test.mjs", "*.spec.mjs", "*.test.cjs", "*.spec.cjs",
    ];

    private static readonly string[] SkipDirs = ["node_modules", "dist", "build", "coverage", ".git", ".vs", "bin", "obj"];

    /// <summary>describe / it / test の呼び出し行。第 1 引数の文字列リテラル（' " ` の 3 種）をタイトルとして拾う。
    /// <c>.each(...)（テーブル付き）</c>・<c>.skip/.only/.concurrent/.todo</c> 修飾も許容する。</summary>
    [GeneratedRegex("""(?<fn>\b(?:describe|it|test))(?:(?:\s*\.\s*(?:skip|only|concurrent|todo|sequential|fails))|(?:\s*\.\s*each(?:\s*\([^)]*\))?))*\s*\(""",
        RegexOptions.Compiled)]
    private static partial Regex TestCall();

    /// <summary>ワークスペースフォルダー配下のテストを走査する（node_modules 等はスキップ）。</summary>
    public static IReadOnlyList<TsDiscoveredTest> Discover(string root)
    {
        var files = new List<string>();
        CollectFiles(root, maxDepth: 8, files);
        var result = new List<TsDiscoveredTest>();
        foreach (var file in files)
        {
            try { result.AddRange(ParseSource(file, File.ReadAllText(file))); }
            catch { /* 読めないファイルは飛ばす */ }
        }
        return result;
    }

    private static void CollectFiles(string dir, int maxDepth, List<string> found)
    {
        try
        {
            foreach (var pattern in TestFilePatterns)
                found.AddRange(Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly));
            if (maxDepth <= 0) return;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (Array.IndexOf(SkipDirs, name) >= 0 || name.StartsWith('.')) continue;
                CollectFiles(sub, maxDepth - 1, found);
            }
        }
        catch { /* アクセス不能ディレクトリは無視 */ }
    }

    /// <summary>1 ファイル分のパース（テスト用に分離）。コメント・文字列をマスクしたソースを
    /// 構文検索に使うため、コメント中の <c>it(...)</c> や import の文字列をテストとして拾わない。
    /// describe の入れ子は、文字列・コメントを除いた波かっこ深度で近似する。</summary>
    internal static List<TsDiscoveredTest> ParseSource(string filePath, string text)
    {
        var result = new List<TsDiscoveredTest>();
        var masked = MaskNonCode(text);
        // (深度, describe 名) のスタック。マッチ位置の深度がこの深度以下になったら閉じたとみなす。
        var describeStack = new Stack<(int Depth, string Name)>();

        int scanned = 0;   // 深度計算済みの位置
        int depth = 0;

        foreach (Match m in TestCall().Matches(masked))
        {
            // 前回位置からマッチ位置までの波かっこと改行を数える。
            for (; scanned < m.Index; scanned++)
            {
                var c = masked[scanned];
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }

            while (describeStack.Count > 0 && depth <= describeStack.Peek().Depth)
                describeStack.Pop();

            if (!TryReadTitle(text, m.Index + m.Length, out var name)) continue;
            var line = 1 + text.AsSpan(0, m.Index).Count('\n');
            if (m.Groups["fn"].Value == "describe")
            {
                describeStack.Push((depth, name));
                continue;
            }

            var title = describeStack.Count == 0
                ? name
                : string.Join(" > ", ReverseNames(describeStack)) + " > " + name;
            var isEach = m.Value.Contains(".each", StringComparison.Ordinal)
                      || m.Value.Contains(". each", StringComparison.Ordinal);
            result.Add(new TsDiscoveredTest(filePath, title, line, isEach));
        }
        return result;
    }

    private static IEnumerable<string> ReverseNames(Stack<(int Depth, string Name)> stack)
    {
        var arr = stack.ToArray();          // Stack の列挙は上から。外側の describe から並べ直す。
        for (var i = arr.Length - 1; i >= 0; i--)
            yield return arr[i].Name;
    }

    /// <summary>コメントと文字列リテラルを空白へ置き換え、改行だけを残す。長さを保つので
    /// 正規表現の位置と元ソースの行番号が一致する。
    /// <para>マスクを外すと<b>そこから先のテストが丸ごと消える</b>（偽の文字列開始が次の引用符まで走り、
    /// 後続の <c>it(</c> の丸かっこまで空白化する）ので、TS/JSX で引用符が地の文に出る形を明示的に避ける：
    /// ① 正規表現リテラル（<c>/'/g</c> 等）は本文ごとマスクして中の引用符を無効化する。
    /// ② <c>'</c> <c>"</c> は<b>同じ行で閉じるときだけ</b>文字列とみなす（JSX の <c>&lt;p&gt;it's…&lt;/p&gt;</c> は
    /// 文字列ではない）。③ それでも解釈が途中で終わったら、マスクを捨てて素のソースで探索する。</para></summary>
    private static string MaskNonCode(string text)
    {
        var chars = text.ToCharArray();
        var state = LexState.Code;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = text[i];
            switch (state)
            {
                case LexState.Code:
                    if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                    {
                        chars[i++] = ' ';
                        chars[i] = ' ';
                        state = LexState.LineComment;
                    }
                    else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                    {
                        chars[i++] = ' ';
                        chars[i] = ' ';
                        state = LexState.BlockComment;
                    }
                    else if (c == '/' && IsRegexPosition(text, i))
                    {
                        i = MaskRegexLiteral(text, chars, i);
                    }
                    else if (c == '`')
                    {
                        chars[i] = ' ';
                        state = LexState.Template;
                    }
                    else if ((c is '\'' or '"') && ClosesOnSameLine(text, i, c))
                    {
                        chars[i] = ' ';
                        state = c == '\'' ? LexState.SingleQuote : LexState.DoubleQuote;
                    }
                    break;
                case LexState.LineComment:
                    if (c == '\n') state = LexState.Code;
                    else chars[i] = ' ';
                    break;
                case LexState.BlockComment:
                    if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                    {
                        chars[i++] = ' ';
                        chars[i] = ' ';
                        state = LexState.Code;
                    }
                    else if (c != '\n') chars[i] = ' ';
                    break;
                case LexState.SingleQuote:
                case LexState.DoubleQuote:
                case LexState.Template:
                    if (c == '\\' && i + 1 < text.Length)
                    {
                        chars[i] = ' ';
                        if (text[i + 1] != '\n') chars[++i] = ' ';
                    }
                    else if ((state == LexState.SingleQuote && c == '\'')
                          || (state == LexState.DoubleQuote && c == '"')
                          || (state == LexState.Template && c == '`'))
                    {
                        chars[i] = ' ';
                        state = LexState.Code;
                    }
                    else if (c != '\n') chars[i] = ' ';
                    break;
            }
        }
        // 文字列・コメントが閉じないまま終わった＝どこかで解釈を外している。そのマスクを信じると
        // 「後ろのテストが全部消える」ほうへ倒れるので、素のソース（マスク前の挙動）へ落とす。
        return state == LexState.Code ? new string(chars) : text;
    }

    /// <summary><c>/</c> が正規表現リテラルの開始位置か（＝除算ではないか）を直前のトークンで近似する。</summary>
    private static bool IsRegexPosition(string text, int slash)
    {
        var i = slash - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i < 0) return true;                                  // 行頭・ファイル先頭
        var c = text[i];
        if (c is ')' or ']' or '}') return false;                // 値の終わりの直後＝除算
        if (!char.IsLetterOrDigit(c) && c is not ('_' or '$')) return true;   // 演算子・かっこ・カンマの後
        // 識別子・数値の後は除算。ただし return / typeof のようなキーワードの後は正規表現。
        var end = i + 1;
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i--;
        return Array.IndexOf(RegexPrecedingKeywords, text[(i + 1)..end]) >= 0;
    }

    /// <summary>直後の <c>/</c> が正規表現リテラルになるキーワード。</summary>
    private static readonly string[] RegexPrecedingKeywords =
        ["return", "typeof", "instanceof", "case", "in", "of", "delete", "void", "do", "else", "yield", "await", "new", "throw"];

    /// <summary>正規表現リテラルの本体を空白へ潰し、閉じ <c>/</c> の位置を返す。行内で閉じなければ
    /// 正規表現ではない（除算）とみなして <paramref name="start"/> をそのまま返す。</summary>
    private static int MaskRegexLiteral(string text, char[] chars, int start)
    {
        var inClass = false;
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') return start;
            if (c == '\\') { i++; continue; }
            if (inClass) { if (c == ']') inClass = false; continue; }
            if (c == '[') { inClass = true; continue; }
            if (c != '/') continue;
            for (var k = start; k <= i; k++) chars[k] = ' ';
            return i;
        }
        return start;
    }

    /// <summary>引用符が同じ行で閉じるか。JS の <c>'</c> <c>"</c> 文字列は行をまたげないので、
    /// 閉じないものは文字列ではない（JSX の地の文のアポストロフィなど）。</summary>
    private static bool ClosesOnSameLine(string text, int quote, char q)
    {
        for (var i = quote + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') return false;
            if (c == '\\') { i++; continue; }
            if (c == q) return true;
        }
        return false;
    }

    private static bool TryReadTitle(string text, int start, out string title)
    {
        title = "";
        var i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length || text[i] is not ('\'' or '"' or '`')) return false;
        var quote = text[i++];
        var begin = i;
        var escaped = false;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c == quote)
            {
                title = text[begin..i];
                return true;
            }
            if (quote != '`' && c == '\n') return false;
        }
        return false;
    }

    private enum LexState { Code, LineComment, BlockComment, SingleQuote, DoubleQuote, Template }
}
