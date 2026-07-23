using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Debug;

/// <summary>VS の「自動変数（Autos）」を netcoredbg 上で近似するための純粋ヘルパ。アダプタは autos スコープを
/// 持たないので、停止行とその直前行のソースから識別子（と <c>a.b.c</c> のメンバアクセス連鎖）を拾い、
/// 評価候補として順序つき・重複なしで返す。実際の値はフレーム文脈で <c>evaluate</c> して得る（VM 側）。
/// あくまでベストエフォートで、評価に失敗した候補は呼び出し側が捨てる。</summary>
public static class AutosExtractor
{
    // 識別子、または a.b.c のメンバアクセス連鎖（先頭は識別子）。メソッド呼び出し () や添字 [] は含めない
    // （副作用・評価失敗を避けるため、素の名前・プロパティ参照だけを候補にする）。
    private static readonly Regex Token = new(
        @"[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*",
        RegexOptions.Compiled);

    /// <summary>評価候補にしない C# キーワード・文脈語（評価してもノイズ/型になるもの）。</summary>
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while", "var", "nameof", "when", "where", "yield", "async", "await",
        "get", "set", "value", "add", "remove",
    };

    /// <summary>評価候補にしない TypeScript / JavaScript のキーワード・ノイズ語。<c>console</c>（log 行のたびに
    /// 巨大オブジェクトが並ぶ）や型注釈語（<c>string</c>/<c>number</c> 等）も落とす。</summary>
    private static readonly HashSet<string> TypeScriptKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "any", "as", "async", "await", "boolean", "break", "case", "catch", "class",
        "const", "continue", "debugger", "declare", "default", "delete", "do", "else", "enum",
        "export", "extends", "false", "finally", "for", "from", "function", "get", "if",
        "implements", "import", "in", "infer", "instanceof", "interface", "is", "keyof", "let",
        "namespace", "never", "new", "null", "number", "object", "of", "override", "private",
        "protected", "public", "readonly", "return", "satisfies", "set", "static", "string",
        "super", "switch", "symbol", "this", "throw", "true", "try", "type", "typeof",
        "undefined", "unique", "unknown", "var", "void", "while", "with", "yield",
        "console", "require", "module", "exports",
    };

    /// <summary>抽出対象の言語（キーワード集合の選択）。</summary>
    public enum AutosLanguage { CSharp, TypeScript }

    /// <summary>ファイル拡張子から言語を判定する（.ts/.js 系 → TypeScript、それ以外 → C#）。</summary>
    public static AutosLanguage LanguageForPath(string? path)
    {
        var ext = path is null ? "" : System.IO.Path.GetExtension(path);
        return ext.ToLowerInvariant() is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs"
            ? AutosLanguage.TypeScript
            : AutosLanguage.CSharp;
    }

    /// <summary>停止行（<paramref name="currentLine"/>）と直前行（<paramref name="previousLine"/>、無ければ null）から
    /// 評価候補を抽出する。出現順・重複なし。コメント/文字列リテラルは除外し、キーワードは捨てる。
    /// メンバアクセス連鎖はルート識別子も併せて候補にする（例 <c>order.Total</c> → <c>order</c> と <c>order.Total</c>）。</summary>
    public static IReadOnlyList<string> ExtractCandidates(string? currentLine, string? previousLine,
        AutosLanguage language = AutosLanguage.CSharp)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keywords = language == AutosLanguage.TypeScript ? TypeScriptKeywords : CSharpKeywords;

        // 直前行 → 現在行の順で拾う（VS も周辺行を見る）。現在行を後にして優先度を上げない＝単純に出現順。
        foreach (var line in new[] { previousLine, currentLine })
            CollectFrom(line, result, seen, keywords);

        return result;
    }

    private static void CollectFrom(string? line, List<string> result, HashSet<string> seen, HashSet<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var code = StripCommentsAndStrings(line);

        foreach (Match m in Token.Matches(code))
        {
            var expr = Normalize(m.Value);
            if (expr.Length == 0) continue;

            // ルート識別子（連鎖の先頭）がキーワードなら連鎖ごと捨てる（this.x 等は this を除いたものを別途拾わない）。
            var root = RootOf(expr);
            if (keywords.Contains(root)) continue;

            // 連鎖のルートだけを単体でも候補に足す（order.Total なら order も見たい）。
            if (!root.Equals(expr, StringComparison.Ordinal) && seen.Add(root))
                result.Add(root);
            if (seen.Add(expr)) result.Add(expr);
        }
    }

    /// <summary>評価結果が「値らしい」か（エラー応答・セッション無しを弾く）。VM が候補を採否するのに使う。</summary>
    public static bool LooksLikeValue(string? evaluated)
        => !string.IsNullOrEmpty(evaluated)
           && !evaluated.StartsWith("(評価エラー", StringComparison.Ordinal)
           && !evaluated.StartsWith("(セッション", StringComparison.Ordinal);

    private static string Normalize(string token) => token.Replace(" ", "").Replace("\t", "");

    private static string RootOf(string expr)
    {
        var dot = expr.IndexOf('.');
        return dot < 0 ? expr : expr[..dot];
    }

    /// <summary>行コメント（<c>//</c>）以降と、二重引用符/文字（<c>'</c>）/テンプレート（<c>`</c>、TS）リテラルの
    /// 中身を空白化して、リテラル内の識別子っぽい文字列を候補に拾わないようにする。簡易（エスケープは深追いしない）。</summary>
    private static string StripCommentsAndStrings(string line)
    {
        var chars = line.ToCharArray();
        var inStr = false;
        var inChar = false;
        var inTemplate = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (inStr)
            {
                if (c == '"') inStr = false; else chars[i] = ' ';
            }
            else if (inChar)
            {
                if (c == '\'') inChar = false; else chars[i] = ' ';
            }
            else if (inTemplate)
            {
                if (c == '`') inTemplate = false; else chars[i] = ' ';
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                for (var j = i; j < chars.Length; j++) chars[j] = ' ';
                break;
            }
            else if (c == '"') { inStr = true; chars[i] = ' '; }
            else if (c == '\'') { inChar = true; chars[i] = ' '; }
            else if (c == '`') { inTemplate = true; chars[i] = ' '; }
        }
        return new string(chars);
    }
}
