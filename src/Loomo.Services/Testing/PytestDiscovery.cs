using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Services.Testing;

/// <summary>pytest のテストソース走査（テストエクスプローラとガターの ▶ の元データ）。dotnet の <c>TestDiscoveryService</c>・
/// TS の <c>TsTestDiscovery</c> と同じくランナーを起動しないベストエフォート：pytest の既定の収集規則
/// （<c>test_*.py</c> / <c>*_test.py</c> の、トップレベルの <c>test*</c> 関数と、<c>Test*</c> クラス／<c>unittest.TestCase</c>
/// 派生クラスの <c>test*</c> メソッド）をインデントで追う。conftest やプラグインでの収集規則の変更は追わない
/// ——外れたテストも実行結果のレポートから行が足されるので、一覧から消えはしない。</summary>
public static partial class PytestDiscovery
{
    /// <summary>テストファイルと見なすパターン（pytest の <c>python_files</c> の既定）。</summary>
    public static readonly string[] TestFilePatterns = ["test_*.py", "*_test.py"];

    /// <summary>走査しないディレクトリ（仮想環境・キャッシュ・成果物）。ドット始まりも飛ばす。</summary>
    public static readonly string[] SkipDirs =
        ["venv", "env", "__pycache__", "node_modules", "build", "dist", "site-packages", "bin", "obj"];

    [GeneratedRegex(@"^(?<indent>[ \t]*)class\s+(?<name>\w+)\s*(?:\((?<bases>[^)]*)\))?\s*:")]
    private static partial Regex ClassLine();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:async\s+)?def\s+(?<name>\w+)\s*\(")]
    private static partial Regex DefLine();

    /// <summary>ワークスペースフォルダー配下のテストを走査する。</summary>
    public static IReadOnlyList<ScriptDiscoveredTest> Discover(string root)
    {
        var files = new List<string>();
        CollectFiles(root, maxDepth: 8, files);
        var result = new List<ScriptDiscoveredTest>();
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

    /// <summary>1 ファイル分のパース（テスト用に分離）。タイトルはノード ID 後半（<c>TestCls::test_x</c>）。
    /// <para>入れ子は (インデント幅, 種別) のスタックで追う：class/def 行が来たら、それ以上深いか同じ幅の要素を閉じる。
    /// 関数の中の def（ヘルパ）や、テストでないクラスのメソッドは拾わない。三連引用符の中は飛ばす
    /// （docstring の例示コードをテストとして拾わないため。行内で開閉が奇数回なら状態を反転する近似）。</para></summary>
    public static List<ScriptDiscoveredTest> ParseSource(string filePath, string text)
    {
        var result = new List<ScriptDiscoveredTest>();
        var stack = new List<(int Indent, bool IsClass, bool IsTestClass, string Name)>();
        var inTripleQuote = false;
        var parametrizePending = false;
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var wasInString = inTripleQuote;
            if ((CountOf(line, "\"\"\"") + CountOf(line, "'''")) % 2 == 1) inTripleQuote = !inTripleQuote;
            if (wasInString) continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('@'))
            {
                if (trimmed.Contains("parametrize", StringComparison.Ordinal)) parametrizePending = true;
                continue;
            }

            var cls = ClassLine().Match(line);
            var def = cls.Success ? Match.Empty : DefLine().Match(line);
            if (!cls.Success && !def.Success) continue;

            var indent = IndentWidth((cls.Success ? cls : def).Groups["indent"].Value);
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            var insideTestClasses = stack.All(s => s.IsClass && s.IsTestClass);

            if (cls.Success)
            {
                var name = cls.Groups["name"].Value;
                var bases = cls.Groups["bases"].Value;
                var isTest = insideTestClasses
                    && (name.StartsWith("Test", StringComparison.Ordinal) || bases.Contains("TestCase", StringComparison.Ordinal));
                stack.Add((indent, true, isTest, name));
                parametrizePending = false;
                continue;
            }

            var func = def.Groups["name"].Value;
            if (func.StartsWith("test", StringComparison.Ordinal) && insideTestClasses)
            {
                var title = stack.Count == 0 ? func : string.Join("::", stack.Select(s => s.Name)) + "::" + func;
                result.Add(new ScriptDiscoveredTest(filePath, title, i + 1, parametrizePending));
            }
            stack.Add((indent, false, false, func));
            parametrizePending = false;
        }
        return result;
    }

    private static int CountOf(string s, string token)
    {
        var count = 0;
        for (var at = s.IndexOf(token, StringComparison.Ordinal); at >= 0; at = s.IndexOf(token, at + token.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>タブは 8 桁に数える（Python の字句規則と同じ）。混在は稀なので比較が保てれば十分。</summary>
    private static int IndentWidth(string indent) => indent.Sum(c => c == '\t' ? 8 : 1);
}
