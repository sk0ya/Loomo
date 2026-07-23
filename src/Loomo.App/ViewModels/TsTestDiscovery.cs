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
        "*.test.js", "*.spec.js", "*.test.jsx", "*.spec.jsx",
        "*.test.mjs", "*.spec.mjs", "*.test.cjs", "*.spec.cjs",
    ];

    private static readonly string[] SkipDirs = ["node_modules", "dist", "build", "coverage", ".git", ".vs", "bin", "obj"];

    /// <summary>describe / it / test の呼び出し行。第 1 引数の文字列リテラル（' " ` の 3 種）をタイトルとして拾う。
    /// <c>.each(...)（テーブル付き）</c>・<c>.skip/.only/.concurrent/.todo</c> 修飾も許容する。</summary>
    [GeneratedRegex("""(?<fn>\bdescribe|\bit|\btest)\s*(?:\.\s*(?:skip|only|concurrent|todo|sequential|fails)\s*)*(?:\.\s*each\s*(?:\([^)]*\)|`[^`]*`)\s*)?\s*\(\s*(?:'(?<t1>[^'\\]*(?:\\.[^'\\]*)*)'|"(?<t2>[^"\\]*(?:\\.[^"\\]*)*)"|`(?<t3>[^`]*)`)""",
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

    /// <summary>1 ファイル分のパース（テスト用に分離）。describe の入れ子は「マッチ位置の波かっこ深度」で近似する：
    /// より深い位置のマッチは直前の describe の配下、同深度以下に戻ったら describe をポップ。</summary>
    internal static List<TsDiscoveredTest> ParseSource(string filePath, string text)
    {
        var result = new List<TsDiscoveredTest>();
        // (深度, describe 名) のスタック。マッチ位置の深度がこの深度以下になったら閉じたとみなす。
        var describeStack = new Stack<(int Depth, string Name)>();

        int scanned = 0;   // 深度計算済みの位置
        int depth = 0;
        int line = 1;

        foreach (Match m in TestCall().Matches(text))
        {
            // 前回位置からマッチ位置までの波かっこと改行を数える（粗い近似：文字列内の {} も数える）。
            for (; scanned < m.Index; scanned++)
            {
                var c = text[scanned];
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == '\n') line++;
            }

            while (describeStack.Count > 0 && depth <= describeStack.Peek().Depth)
                describeStack.Pop();

            var name = m.Groups["t1"].Success ? m.Groups["t1"].Value
                     : m.Groups["t2"].Success ? m.Groups["t2"].Value
                     : m.Groups["t3"].Value;
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
}
