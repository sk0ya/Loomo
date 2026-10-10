using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// テストと実装の対応（Rider の Navigate to Test／Test Subject 相当）。純ロジック（テスト対象）。
/// 命名規約だけで決める——テストランナーの検出結果（§28.14）は「どのテストが在るか」は知っていても、
/// 「何のテストか」は知らないため。対応は各言語でいちばん普通の規約に絞る：
/// <list type="bullet">
/// <item>C#：<c>Foo.cs</c> ⇔ <c>FooTests.cs</c>／<c>FooTest.cs</c>／<c>Foo.Tests.cs</c></item>
/// <item>TS/JS：<c>foo.ts</c> ⇔ <c>foo.test.ts</c>／<c>foo.spec.ts</c>（tsx・js・jsx・mts・cts も同じ）</item>
/// <item>Python：<c>foo.py</c> ⇔ <c>test_foo.py</c>／<c>foo_test.py</c></item>
/// </list>
/// </summary>
public static class RelatedTestFiles
{
    private static readonly string[] CSharpSuffixes = ["Tests", "Test", ".Tests"];
    private static readonly string[] ScriptMarkers = [".test", ".spec"];
    private static readonly HashSet<string> ScriptExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ts", ".tsx", ".js", ".jsx", ".mts", ".cts", ".mjs", ".cjs" };

    /// <summary>このファイルがテストか。</summary>
    public static bool IsTestFile(string path) => SubjectNames(path).Count > 0;

    /// <summary>行き先になりうるファイル名（テストなら実装、実装ならテスト）。対応が無い拡張子は空。</summary>
    public static IReadOnlyList<string> CounterpartNames(string path)
    {
        var subjects = SubjectNames(path);
        return subjects.Count > 0 ? subjects : TestNames(path);
    }

    /// <summary>実装ファイル名から、そのテストのファイル名の候補。</summary>
    public static IReadOnlyList<string> TestNames(string path)
    {
        var ext = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(stem))
            return [];
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return CSharpSuffixes.Select(s => stem + s + ext).ToArray();
        if (ScriptExtensions.Contains(ext))
            return ScriptMarkers.Select(m => stem + m + ext).ToArray();
        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase))
            return [$"test_{stem}{ext}", $"{stem}_test{ext}"];
        return [];
    }

    /// <summary>テストファイル名から、実装のファイル名の候補。テストでなければ空。</summary>
    public static IReadOnlyList<string> SubjectNames(string path)
    {
        var ext = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(stem))
            return [];
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var suffix in CSharpSuffixes.OrderByDescending(s => s.Length))
            {
                if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal))
                    return [stem[..^suffix.Length] + ext];
            }
            return [];
        }
        if (ScriptExtensions.Contains(ext))
        {
            foreach (var marker in ScriptMarkers)
            {
                if (stem.Length > marker.Length && stem.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                {
                    var subject = stem[..^marker.Length];
                    // foo.test.ts の実装は foo.ts だけでなく foo.tsx／foo.js のこともある。
                    return new[] { ext }.Concat(ScriptExtensions.Where(e => !e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                        .Select(e => subject + e).ToArray();
                }
            }
            return [];
        }
        if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            if (stem.StartsWith("test_", StringComparison.Ordinal) && stem.Length > 5)
                return [stem[5..] + ext];
            if (stem.EndsWith("_test", StringComparison.Ordinal) && stem.Length > 5)
                return [stem[..^5] + ext];
        }
        return [];
    }

    /// <summary>
    /// 見つかったファイルを「近い順」に並べる。近さは元ファイルとのフォルダーの共通部分の長さ
    /// （<c>src/Foo/Bar.cs</c> と <c>tests/Foo.Tests/BarTests.cs</c> は <c>Foo</c> の分だけ近い）——同じ名前の
    /// 実装が複数のプロジェクトにあるとき、テストの隣にあるものを先に出す。
    /// </summary>
    public static IReadOnlyList<string> RankByProximity(string from, IEnumerable<string> found)
    {
        var fromParts = Parts(from);
        return found
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !string.Equals(p, from, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => SharedSegments(fromParts, Parts(p)))
            .ThenBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] Parts(string path)
        => (Path.GetDirectoryName(path) ?? "")
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(StripTestDecoration)
            .ToArray();

    /// <summary>フォルダー名から「テストらしさ」を落とす（<c>Foo.Tests</c> → <c>Foo</c>、<c>tests</c> → 空）。</summary>
    private static string StripTestDecoration(string segment)
    {
        foreach (var suffix in new[] { ".Tests", ".Test", ".UnitTests", "Tests", "Test" })
        {
            if (segment.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return segment[..^suffix.Length];
        }
        return segment is "__tests__" or "test" or "tests" or "spec" ? "" : segment;
    }

    private static int SharedSegments(string[] a, string[] b)
    {
        var set = new HashSet<string>(a.Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
        return b.Count(s => s.Length > 0 && set.Contains(s));
    }
}
