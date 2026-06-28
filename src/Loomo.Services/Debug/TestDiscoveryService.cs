using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>ワークスペースの <c>*.cs</c> を走査して xUnit/NUnit/MSTest のテストメソッドを拾う、ビルドを伴わない高速探索。
/// 旧来の <c>dotnet test --list-tests</c> は全プロジェクトをビルドするため遅い・契機が手動だったのに対し、これは
/// ソースの属性（<c>[Fact]</c>/<c>[Theory]</c>/<c>[Test]</c>/<c>[TestMethod]</c>）から一覧を作るのでミリ秒級で済み、
/// バックグラウンド自動収集に向く。実行（結果の突き合わせ）は従来どおり <c>dotnet test</c>＋TRX が担う。</summary>
public sealed class TestDiscoveryService : ITestDiscoveryService
{
    /// <summary>走査から除外するディレクトリ名（ビルド成果物・VCS・依存物）。</summary>
    private static readonly string[] ExcludedDirs = { "bin", "obj", "artifacts", ".git", ".vs", "node_modules" };

    public IReadOnlyList<DiscoveredTest> Discover(string root)
    {
        var results = new List<DiscoveredTest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return results;

        foreach (var file in EnumerateCsFiles(root))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            // 高速プレフィルタ：テスト属性を含まないファイルは解析しない（生成 .cs・非テストの大半を弾く）。
            if (text.IndexOf("Fact", StringComparison.Ordinal) < 0 &&
                text.IndexOf("Theory", StringComparison.Ordinal) < 0 &&
                text.IndexOf("[Test", StringComparison.Ordinal) < 0)
                continue;

            foreach (var t in TestSourceParser.Parse(text))
                if (seen.Add(t.FullyQualifiedName)) results.Add(t);
        }
        return results;
    }

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

/// <summary>C# ソース 1 ファイル分から、テスト属性を持つメソッドの完全名を取り出す純粋関数。
/// 行ベースで namespace/class と直前のテスト属性を追跡する軽量パーサ（Roslyn 非依存）。コメント・文字列リテラルは
/// 事前に空白化して誤検出を避ける。入れ子クラス・複数 namespace のネストなど稀なケースは best-effort。</summary>
public static class TestSourceParser
{
    // file-scoped / ブロックいずれの namespace 宣言にも一致（最後に見たものを採用）。
    private static readonly Regex NamespaceRe = new(@"\bnamespace\s+([\w.]+)", RegexOptions.Compiled);
    // class / record 宣言（ジェネリックは名前まで）。struct/record struct はテストの器として稀なので class/record のみ。
    private static readonly Regex ClassRe = new(@"\b(?:class|record)\s+(\w+)", RegexOptions.Compiled);
    // テストメソッドを示す属性（xUnit: Fact/Theory、NUnit: Test、MSTest: TestMethod）。
    private static readonly Regex TestAttrRe = new(@"\[\s*(Fact|Theory|Test|TestMethod)\b", RegexOptions.Compiled);
    // 行頭の属性群（[...] の連なり）。メソッド名探索の前に取り除く（[InlineData(..)] を名前と誤認しないため）。
    private static readonly Regex AttrPrefixRe = new(@"^(?:\s*\[[^\]]*\]\s*)+", RegexOptions.Compiled);
    // メソッド宣言の名前（戻り値型・修飾子の後、'(' の直前のトークン）。ジェネリックメソッドは名前まで。
    private static readonly Regex MethodRe = new(@"(\w+)\s*(?:<[^>]*>)?\s*\(", RegexOptions.Compiled);

    public static IReadOnlyList<DiscoveredTest> Parse(string source)
    {
        var results = new List<DiscoveredTest>();
        var sanitized = StripCommentsAndStrings(source);
        var lines = sanitized.Replace("\r", "").Split('\n');

        var ns = "";
        var className = "";
        var pending = false;       // テスト属性を見たがまだメソッドに結びついていない
        var pendingTheory = false; // その属性が複数ケース（Theory 等）か

        foreach (var line in lines)
        {
            var nsm = NamespaceRe.Match(line);
            if (nsm.Success) ns = nsm.Groups[1].Value;

            var cm = ClassRe.Match(line);
            if (cm.Success)
            {
                var c = cm.Groups[1].Value;
                // "record struct Foo" 等で struct/class を名前に拾わないようガード。
                if (c is not ("struct" or "class")) className = c;
            }

            var am = TestAttrRe.Match(line);
            if (am.Success)
            {
                pending = true;
                if (string.Equals(am.Groups[1].Value, "Theory", StringComparison.Ordinal)) pendingTheory = true;
            }

            if (pending && className.Length > 0)
            {
                // 行頭の属性を落としてからメソッド名を探す（属性のみの行は空になり、メソッド行まで pending を持ち越す）。
                var stripped = AttrPrefixRe.Replace(line, "");
                var mm = MethodRe.Match(stripped);
                if (mm.Success)
                {
                    var method = mm.Groups[1].Value;
                    var prefix = ns.Length > 0 ? ns + "." + className : className;
                    results.Add(new DiscoveredTest(prefix + "." + method, pendingTheory));
                    pending = false;
                    pendingTheory = false;
                }
            }
        }
        return results;
    }

    /// <summary>コメント（行/ブロック）と文字列・文字リテラルを同じ長さの空白へ潰す（改行は保持）。
    /// 目的はコメントアウトされたコードや文字列中の <c>class</c>/<c>[Fact]</c> 等の誤検出を防ぐこと。
    /// 補間/逐語的文字列も中身を空白化する（行ベース解析にはそれで十分）。</summary>
    private static string StripCommentsAndStrings(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            char d = i + 1 < n ? s[i + 1] : '\0';

            if (c == '/' && d == '/')                       // 行コメント
            {
                while (i < n && s[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }
            if (c == '/' && d == '*')                       // ブロックコメント
            {
                sb.Append("  "); i += 2;
                while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/'))
                { sb.Append(s[i] == '\n' ? '\n' : ' '); i++; }
                if (i < n) { sb.Append("  "); i += 2; }
                continue;
            }
            if (c == '@' && d == '"')                       // 逐語的文字列 @"...""..."
            {
                sb.Append("  "); i += 2;
                while (i < n)
                {
                    if (s[i] == '"')
                    {
                        if (i + 1 < n && s[i + 1] == '"') { sb.Append("  "); i += 2; continue; }
                        sb.Append(' '); i++; break;
                    }
                    sb.Append(s[i] == '\n' ? '\n' : ' '); i++;
                }
                continue;
            }
            if (c == '"')                                   // 通常文字列（補間 $"..." も中身は空白化）
            {
                sb.Append(' '); i++;
                while (i < n && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < n) { sb.Append("  "); i += 2; continue; }
                    sb.Append(s[i] == '\n' ? '\n' : ' '); i++;
                }
                if (i < n) { sb.Append(' '); i++; }
                continue;
            }
            if (c == '\'')                                  // 文字リテラル
            {
                sb.Append(' '); i++;
                while (i < n && s[i] != '\'')
                {
                    if (s[i] == '\\' && i + 1 < n) { sb.Append("  "); i += 2; continue; }
                    sb.Append(' '); i++;
                }
                if (i < n) { sb.Append(' '); i++; }
                continue;
            }

            sb.Append(c); i++;
        }
        return sb.ToString();
    }
}
