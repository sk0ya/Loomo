using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.CSharp.Testing;

/// <summary>ワークスペースの <c>*.cs</c> を走査して xUnit/NUnit/MSTest のテストメソッドを拾う、ビルドを伴わない高速探索。
/// 旧来の <c>dotnet test --list-tests</c> は全プロジェクトをビルドするため遅い・契機が手動だったのに対し、これは
/// Roslyn の構文木から属性と宣言位置を読むので、コメント・文字列・入れ子型を誤認しにくい。
/// 実行（結果の突き合わせ）は従来どおり <c>dotnet test</c>＋TRX が担う。</summary>
public sealed class TestDiscoveryService : ITestDiscoveryService
{
    /// <summary>走査から除外するディレクトリ名（ビルド成果物・VCS・依存物）。</summary>
    private static readonly string[] ExcludedDirs = { "bin", "obj", "artifacts", ".git", ".vs", "node_modules" };

    public IReadOnlyList<DiscoveredTest> Discover(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];
        return DiscoverFiles(EnumerateCsFiles(root));
    }

    /// <summary>選択中の構成／TFMで実際にCompileされるテストプロジェクトだけを探索する。
    /// ソース全体の推測探索と違い、除外Compile・別TFM・アプリプロジェクトのテスト属性を一覧へ混ぜない。</summary>
    public IReadOnlyList<DiscoveredTest> Discover(SolutionModel solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        if (solution.State != ProjectLoadState.Ready) return [];

        var files = solution.Projects
            .Where(project => project.State == ProjectLoadState.Ready && project.IsTestProject)
            .SelectMany(project => project.SelectedTargetFrameworkModel?.CompileFiles ?? [])
            .Where(item => string.Equals(Path.GetExtension(item.FullPath), ".cs",
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.FullPath);
        return DiscoverFiles(files);
    }

    private static IReadOnlyList<DiscoveredTest> DiscoverFiles(IEnumerable<string> files)
    {
        var results = new List<DiscoveredTest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            // 属性が無い通常の C# ファイルは構文解析しない。DataTestMethod なども含めておく。
            if (!ContainsTestMarker(text)) continue;

            foreach (var t in TestSourceParser.Parse(text, file))
                if (seen.Add(t.FullyQualifiedName)) results.Add(t);
        }
        return results;
    }

    private static bool ContainsTestMarker(string text)
        => text.IndexOf("Fact", StringComparison.Ordinal) >= 0
            || text.IndexOf("Theory", StringComparison.Ordinal) >= 0
            || text.IndexOf("Test", StringComparison.Ordinal) >= 0
            || text.IndexOf("DataRow", StringComparison.Ordinal) >= 0
            || text.IndexOf("DynamicData", StringComparison.Ordinal) >= 0;

    /// <summary>除外ディレクトリを避けつつ配下の <c>*.cs</c> を列挙する（権限エラー等は握りつぶしてスキップ）。</summary>
    private static IEnumerable<string> EnumerateCsFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(dir); }
            catch { subdirs = Array.Empty<string>(); }
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (Array.Exists(ExcludedDirs, e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                stack.Push(sub);
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.cs"); }
            catch { files = Array.Empty<string>(); }
            foreach (var f in files) yield return f;
        }
    }
}

/// <summary>C# ソース 1 ファイル分から、テスト属性を持つメソッドの完全名と宣言位置を取り出す純粋関数。
/// Roslyn の構文木を使うため、コメント・文字列・属性の改行をコードとして誤認せず、namespace と入れ子型を
/// メソッド自身から逆引きする。これは test adapter の実行結果を置き換えるものではなく、編集時に即時表示する
/// 構文ベースの候補一覧である。</summary>
public static class TestSourceParser
{
    private static readonly HashSet<string> TestAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fact", "Theory",                    // xUnit
        "Test", "TestCase", "TestCaseSource", // NUnit
        "TestMethod", "DataTestMethod",        // MSTest
        "DataRow", "DynamicData",
    };

    private static readonly HashSet<string> ParameterizedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Theory", "TestCase", "TestCaseSource", "DataTestMethod", "DataRow", "DynamicData",
    };

    public static IReadOnlyList<DiscoveredTest> Parse(string source) => Parse(source, null);

    /// <summary><paramref name="filePath"/> を添えると、各テストの宣言位置（ファイル＋1 始まりの行）も返す。
    /// 行はテスト属性の行ではなくメソッド宣言の行である。</summary>
    public static IReadOnlyList<DiscoveredTest> Parse(string source, string? filePath)
    {
        if (string.IsNullOrEmpty(source)) return Array.Empty<DiscoveredTest>();

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var results = new List<DiscoveredTest>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var attributes = method.AttributeLists
                .SelectMany(list => list.Attributes)
                .ToArray();
            var attributeNames = attributes
                .Select(attribute => AttributeName(attribute.Name))
                .ToArray();
            var testAttributes = attributeNames.Where(TestAttributes.Contains).ToArray();
            if (testAttributes.Length == 0) continue;

            var namespaceParts = method.Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse()
                .Select(n => n.Name.ToString())
                .Where(n => n.Length > 0)
                .ToList();
            var typeParts = method.Ancestors()
                .OfType<TypeDeclarationSyntax>()
                .Reverse()
                .Select(t => t.Identifier.ValueText)
                .Where(n => n.Length > 0)
                .ToList();

            var nameParts = namespaceParts.Concat(typeParts).Append(method.Identifier.ValueText);
            var fqn = string.Join(".", nameParts);
            var line = tree.GetLineSpan(method.Identifier.Span).StartLinePosition.Line + 1;
            var isParameterized = testAttributes.Any(ParameterizedAttributes.Contains);
            results.Add(new DiscoveredTest(fqn, isParameterized, filePath, line,
                FindSkipReason(attributes), FindTraits(attributes)));
        }

        return results;
    }

    private static string AttributeName(NameSyntax name)
    {
        var text = name.ToString();
        var lastDot = text.LastIndexOf('.');
        if (lastDot >= 0) text = text[(lastDot + 1)..];
        return text.EndsWith("Attribute", StringComparison.Ordinal)
            ? text[..^"Attribute".Length]
            : text;
    }

    private static string? FindSkipReason(IEnumerable<AttributeSyntax> attributes)
    {
        foreach (var attribute in attributes)
        {
            var name = AttributeName(attribute.Name);
            if (name is "Ignore" or "Skip")
                return FirstStringArgument(attribute) ?? name;

            if (name is "Fact" or "Theory")
            {
                var skip = attribute.ArgumentList?.Arguments
                    .FirstOrDefault(a => string.Equals(a.NameEquals?.Name.Identifier.ValueText,
                        "Skip", StringComparison.OrdinalIgnoreCase));
                if (skip is not null) return StringValue(skip.Expression) ?? "Skip";
            }
        }
        return null;
    }

    private static IReadOnlyList<string> FindTraits(IEnumerable<AttributeSyntax> attributes)
    {
        var traits = new List<string>();
        foreach (var attribute in attributes)
        {
            var name = AttributeName(attribute.Name);
            var args = attribute.ArgumentList?.Arguments ?? default;
            if (name == "Trait" && args.Count >= 2)
            {
                traits.Add($"{StringValue(args[0].Expression) ?? args[0].Expression.ToString()}="
                    + $"{StringValue(args[1].Expression) ?? args[1].Expression.ToString()}");
            }
            else if (name is "Category" or "TestCategory" && args.Count >= 1)
            {
                traits.Add(StringValue(args[0].Expression) ?? args[0].Expression.ToString());
            }
        }
        return traits;
    }

    private static string? FirstStringArgument(AttributeSyntax attribute)
        => attribute.ArgumentList?.Arguments.Count > 0
            ? StringValue(attribute.ArgumentList.Arguments[0].Expression)
            : null;

    private static string? StringValue(ExpressionSyntax expression)
        => expression is LiteralExpressionSyntax literal && literal.Kind() == SyntaxKind.StringLiteralExpression
            ? literal.Token.ValueText
            : null;

    /// <summary>旧来の行ベースパーサ向けに公開していた属性接頭辞の小さなユーティリティ。
    /// 外部の純ロジックテストとの互換性のため残している。テスト探索本体は使用しない。</summary>
    internal static string StripAttributePrefix(string line, ref int depth)
    {
        var i = 0;
        while (i < line.Length)
        {
            if (depth == 0)
            {
                if (char.IsWhiteSpace(line[i])) { i++; continue; }
                if (line[i] != '[') break;
                depth = 1;
                i++;
                continue;
            }
            if (line[i] == '[') depth++;
            else if (line[i] == ']') depth--;
            i++;
        }
        return i >= line.Length ? "" : line[i..];
    }
}
