using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Services;

/// <summary>
/// 無題のバッファ（パスの無いエディタタブ）の言語を、本文から推定する（Zed の untitled バッファの言語自動判定）。
/// 名前はエディタの構文ハイライトの言語名（<c>JSON</c>・<c>C#</c>・<c>Python</c> …）。
/// <para>確かな手がかり（シバン・JSON として読める・<c>&lt;?xml</c>・<c>@echo off</c>）はそれだけで決め、
/// 無ければ頭の行の特徴を言語ごとに数えて、はっきり勝つものだけを採る——競り合いや弱い一致では null
/// （外れた色付けは、色が無いより読みにくい）。純粋で、見るのは頭の <see cref="MaxLines"/> 行だけ。</para>
/// </summary>
public static partial class UntitledLanguageDetector
{
    /// <summary>見る行数。貼り付けた大きな本文でも、判定の手間はここで頭打ちにする。</summary>
    public const int MaxLines = 60;

    /// <summary>JSON として丸ごと読んでみる上限（これより大きければ頭の形だけで決める）。</summary>
    private const int MaxJsonProbe = 256 * 1024;

    private sealed record Rule(string Language, int Weight, Regex Pattern);

    private static readonly Rule[] Rules =
    [
        new("C#", 3, CSharpUsing()), new("C#", 3, CSharpNamespace()), new("C#", 2, CSharpMember()),
        new("C#", 2, new Regex(@"\bConsole\.Write(Line)?\(|\bvar \w+ = new\b|\basync Task\b", RegexOptions.Compiled)),
        new("Python", 3, new Regex(@"^\s*def \w+\(.*\)\s*(->.*)?:\s*$", RegexOptions.Compiled)),
        new("Python", 2, new Regex(@"^\s*(from [\w.]+ import |import [\w.]+(\s+as \w+)?\s*$)", RegexOptions.Compiled)),
        new("Python", 2, new Regex(@"^\s*(class \w+(\(.*\))?:|if __name__ == |elif |print\()", RegexOptions.Compiled)),
        new("TypeScript", 3, new Regex(@"^\s*(export\s+)?(interface|type) \w+.*[={]|:\s*(string|number|boolean)\b|\bas const\b", RegexOptions.Compiled)),
        new("JavaScript", 2, new Regex(@"^\s*(const|let) \w+ = |\bfunction \w*\s*\(|=>\s*[{(]|\bconsole\.log\(|\brequire\(['""]", RegexOptions.Compiled)),
        new("JavaScript", 2, new Regex(@"^\s*import .+ from ['""]|^\s*export (default|const|function)\b", RegexOptions.Compiled)),
        new("PowerShell", 3, new Regex(@"\b(Get|Set|New|Remove|Write|Invoke|Start|Test)-[A-Z]\w+|^\s*param\s*\(", RegexOptions.Compiled)),
        new("PowerShell", 2, new Regex(@"^\s*\$\w+\s*=|\$_\.|\$env:", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        new("Shell", 2, new Regex(@"^\s*(export \w+=|echo ""|if \[\[? |fi\s*$|done\s*$|\w+\(\)\s*\{)", RegexOptions.Compiled)),
        new("SQL", 3, new Regex(@"^\s*(SELECT\s.+\sFROM\s|INSERT INTO\s|UPDATE \w+ SET\s|DELETE FROM\s|CREATE (TABLE|VIEW|INDEX)\s|ALTER TABLE\s|WITH \w+ AS\s*\()", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        // 行をまたいだ SELECT … FROM … WHERE。大文字だけを見る（Python の `from x import` と取り違えない）。
        new("SQL", 2, new Regex(@"^\s*(SELECT|FROM|WHERE|INNER JOIN|LEFT JOIN|JOIN|GROUP BY|ORDER BY|HAVING|VALUES)\b", RegexOptions.Compiled)),
        new("Rust", 3, new Regex(@"^\s*(pub )?fn \w+(<.*>)?\(|^\s*use std::|\blet mut\b|^\s*impl\b|^\s*#\[derive\(", RegexOptions.Compiled)),
        new("Go", 3, new Regex(@"^\s*package \w+\s*$|^\s*func (\(.*\) )?\w+\(|:= ", RegexOptions.Compiled)),
        new("C/C++", 3, new Regex(@"^\s*#include\s*[<""]|^\s*(int|void) main\s*\(|\bstd::", RegexOptions.Compiled)),
        new("Markdown", 3, new Regex(@"^#{1,6} \S|^```", RegexOptions.Compiled)),
        new("Markdown", 1, new Regex(@"^\s*([-*+] |\d+\. )\S|\[[^\]]+\]\([^)]+\)|^> ", RegexOptions.Compiled)),
        new("YAML", 2, new Regex(@"^[\w-]+:\s*(\S.*)?$", RegexOptions.Compiled)),
        new("YAML", 1, new Regex(@"^\s+- \w+", RegexOptions.Compiled)),
        new("TOML", 3, new Regex(@"^\[[\w.-]+\]\s*$", RegexOptions.Compiled)),
        new("TOML", 2, new Regex(@"^\w[\w-]* = (""|\d|true|false|\[)", RegexOptions.Compiled)),
        new("CSS", 3, new Regex(@"^\s*([.#]?[\w-]+(\s*[>,+~]\s*[.#]?[\w-]+)*|@media.*)\s*\{\s*$", RegexOptions.Compiled)),
        new("CSS", 2, new Regex(@"^\s*[\w-]+\s*:\s*[^;]+;\s*$", RegexOptions.Compiled)),
        new("Lua", 3, new Regex(@"^\s*local \w+ = |^\s*(local )?function [\w.:]+\(.*\)\s*$", RegexOptions.Compiled)),
        new("Ruby", 3, new Regex(@"^\s*(def \w+[?!]?(\(.*\))?\s*$|require '|puts |attr_accessor\b)", RegexOptions.Compiled)),
    ];

    /// <summary>推定した言語名。分からなければ null。</summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');

        if (Shebang(trimmed) is { } fromShebang) return fromShebang;
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(trimmed, @"^<(!DOCTYPE|[A-Za-z][\w:.-]*)[\s>/]")) return "XML";
        if (trimmed.StartsWith("@echo off", StringComparison.OrdinalIgnoreCase)) return "Batch";
        if (trimmed[0] is '{' or '[' && LooksLikeJson(trimmed)) return "JSON";
        if (trimmed.StartsWith("---\n", StringComparison.Ordinal) || trimmed.StartsWith("---\r\n", StringComparison.Ordinal))
            return LooksLikeMarkdownAfterFrontMatter(trimmed) ? "Markdown" : "YAML";

        var lines = trimmed.Replace("\r\n", "\n").Split('\n').Take(MaxLines).Where(l => l.Trim().Length > 0).ToList();
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            // 1行が同じ言語の規則に何度当たっても、その言語へは一番重い1つだけ数える（長い1行で勝たせない）。
            foreach (var group in Rules.Where(r => r.Pattern.IsMatch(line)).GroupBy(r => r.Language))
                scores[group.Key] = scores.GetValueOrDefault(group.Key) + group.Max(r => r.Weight);
        }
        // TypeScript の手がかりは JavaScript の上位互換：両方当たれば TypeScript に寄せる。
        if (scores.TryGetValue("TypeScript", out var ts) && scores.TryGetValue("JavaScript", out var js))
            scores["TypeScript"] = ts + js;

        var ranked = scores.OrderByDescending(p => p.Value).ToList();
        if (ranked.Count == 0) return null;
        var best = ranked[0];
        // 弱い一致（YAML の「key: value」1行・Markdown の箇条書き1行など）では決めない。競り合いでも決めない。
        if (best.Value < 3) return null;
        var tsOverJs = best.Key == "TypeScript" && ranked.Count > 1 && ranked[1].Key == "JavaScript";
        if (ranked.Count > 1 && !tsOverJs && ranked[1].Value * 3 > best.Value * 2) return null;
        return best.Key;
    }

    private static string? Shebang(string text)
    {
        if (!text.StartsWith("#!", StringComparison.Ordinal)) return null;
        var first = text.Split('\n', 2)[0].ToLowerInvariant();
        if (first.Contains("python")) return "Python";
        if (first.Contains("node") || first.Contains("deno") || first.Contains("bun")) return "JavaScript";
        if (first.Contains("pwsh") || first.Contains("powershell")) return "PowerShell";
        if (first.Contains("ruby")) return "Ruby";
        if (first.Contains("lua")) return "Lua";
        if (Regex.IsMatch(first, @"\b(ba|z|k|da)?sh\b")) return "Shell";
        return null;
    }

    private static bool LooksLikeJson(string text)
    {
        if (text.Length <= MaxJsonProbe)
        {
            try
            {
                using var _ = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                return true;
            }
            catch (JsonException)
            {
                // 書きかけ（閉じ括弧がまだ無い）は下の形で見る。
            }
        }
        // `{ "key": ` / `[ {` / `[ "` で始まっていれば、書きかけの JSON とみなす。
        return Regex.IsMatch(text, @"^\{\s*(""[^""]*""\s*:|\}|$)") || Regex.IsMatch(text, @"^\[\s*([{\[""\d\]]|true|false|null|$)");
    }

    private static bool LooksLikeMarkdownAfterFrontMatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var close = Array.IndexOf(lines, "---", 1);
        return close > 0 && lines.Skip(close + 1).Any(l => l.Trim().Length > 0);
    }

    [GeneratedRegex(@"^\s*using (static )?[A-Z][\w.]*\s*;\s*$")]
    private static partial Regex CSharpUsing();

    [GeneratedRegex(@"^\s*namespace [A-Z][\w.]*\s*[;{]?\s*$")]
    private static partial Regex CSharpNamespace();

    [GeneratedRegex(@"^\s*(public|private|internal|protected)( static| sealed| abstract| partial| async| override| readonly)* (class|record|struct|interface|enum|void|string|int|bool|Task)\b")]
    private static partial Regex CSharpMember();
}
