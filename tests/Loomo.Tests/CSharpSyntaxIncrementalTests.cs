using System.Reflection;
using Editor.Core.Engine;
using Editor.Core.Syntax;
using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// Loomo の C# 字句器（<see cref="CSharpSyntaxLanguage"/>）を、エディタが編集のたびに<b>変わった行から先だけ</b>
/// 分け直しても、毎回ファイル全体を分けた結果と寸分違わないこと。生文字列・補間・逐語文字列・属性・
/// ブロックコメントのように行をまたいで状態を持ち越す書き方を、開いたり閉じたりする編集を混ぜて確かめる。
/// </summary>
public sealed class CSharpSyntaxIncrementalTests
{
    private static readonly string[] Fragments =
    [
        "namespace N;", "public sealed class C : Base", "{", "}", "", "    ",
        "    // comment", "    /// <summary>doc <see cref=\"X\"/></summary>", "/*", "*/", "/* inline */ int x = 1;",
        "    var s = \"text\";", "    var v = @\"verbatim", "still verbatim \"\" quote\";",
        "    var r = \"\"\"", "raw content {braces}", "    \"\"\";", "    var rr = $$\"\"\"", "{{value}} raw interp", "\"\"\";",
        "    var i = $\"a {b} c\";", "    var open = $\"start {", "middle}", " end\";", "    var multi = $@\"x {y", "} z\";",
        "    [Attribute(", "        Name = \"x\")]", "    [Fact]", "    [", "    ]", "#if DEBUG", "#endif",
        "    char c = '\\'';", "    var 日本語 = \"字\";", "    int Method(int a) => a + 1;", "\t\tindented",
        "    var nested = $\"{(cond ? $\"{a}\" : \"b\")}\";",
    ];

    private static SyntaxEngine NewEngine()
    {
        var services = VimEngineServices.CreateIsolated();
        CSharpEditorIntegration.Configure(services);
        var engine = new SyntaxEngine(services.SyntaxLanguages);
        engine.DetectLanguage("Feature.cs");
        return engine;
    }

    [Fact]
    public void LoomoCSharp_IsTokenizedLineByLine()
        => Assert.IsAssignableFrom<ILineStateSyntaxLanguage>(new CSharpSyntaxLanguage());

    [Fact]
    public void LineByLineTokenize_MatchesThePreviousWholeFileLoop()
    {
        // 変更前の Tokenize（状態を3つの変数で持ち回すループ）を、同じ1行の処理（private な TokenizeLine）で再現した物差し。
        var tokenizeLine = typeof(CSharpSyntaxLanguage).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "TokenizeLine" && m.GetParameters().Length == 5);
        var noneState = typeof(CSharpSyntaxLanguage).GetNestedType("StringState", BindingFlags.NonPublic)!
            .GetProperty("None", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);

        LineTokens[] Previous(string[] lines)
        {
            var result = new LineTokens[lines.Length];
            object?[] args = [null, null, false, noneState, 0];
            for (int i = 0; i < lines.Length; i++)
            {
                var tokens = new List<SyntaxToken>();
                args[0] = lines[i];
                args[1] = tokens;
                tokenizeLine.Invoke(null, args);   // ref の引数は args の中で書き換わって次の行へ持ち越される
                result[i] = new LineTokens(i, [.. tokens]);
            }
            return result;
        }

        for (int seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            var lines = Enumerable.Range(0, random.Next(0, 60)).Select(_ => Fragments[random.Next(Fragments.Length)]).ToArray();
            AssertSame(Previous(lines), new CSharpSyntaxLanguage().Tokenize(lines), $"seed={seed}");
        }
    }

    [Fact]
    public void RandomEdits_MatchFullTokenization()
    {
        for (int seed = 0; seed < 60; seed++)
        {
            var random = new Random(seed * 101 + 3);
            var engine = NewEngine();
            var lines = Enumerable.Range(0, random.Next(0, 60)).Select(_ => Pick(random)).ToList();
            for (int step = 0; step < 60; step++)
            {
                Mutate(lines, random);
                var snapshot = lines.ToArray();
                engine.Invalidate(); // エンジンは本文が変わるたびにこれを呼ぶ
                AssertSame(new CSharpSyntaxLanguage().Tokenize(snapshot), engine.Tokenize(snapshot), $"seed={seed} step={step}");
            }
        }
    }

    [Fact]
    public void TypingInsideAMethod_RetokenizesOnlyThatLine()
    {
        var engine = NewEngine();
        var lines = Enumerable.Range(0, 3000).Select(i => $"    var v{i} = $\"x {{i}}\"; // {i}").ToArray();
        var before = engine.Tokenize(lines);

        var edited = (string[])lines.Clone();
        edited[1500] = "    var typed = 1;";
        engine.Invalidate();
        var after = engine.Tokenize(edited);

        Assert.Same(before[100].Tokens, after[100].Tokens);
        Assert.Same(before[2900].Tokens, after[2900].Tokens);
        AssertSame(new CSharpSyntaxLanguage().Tokenize(edited), after, "typing");
    }

    private static string Pick(Random random) => Fragments[random.Next(Fragments.Length)];

    private static void Mutate(List<string> lines, Random random)
    {
        switch (random.Next(6))
        {
            case 0: lines.Insert(random.Next(lines.Count + 1), Pick(random)); break;
            case 1: if (lines.Count > 0) lines.RemoveAt(random.Next(lines.Count)); break;
            case 2 when lines.Count > 0:
            {
                int i = random.Next(lines.Count);
                int at = random.Next(lines[i].Length + 1);
                lines[i] = lines[i][..at] + Pick(random) + lines[i][at..];
                break;
            }
            case 3 when lines.Count > 0:
            {
                int i = random.Next(lines.Count);
                if (lines[i].Length > 0) lines[i] = lines[i].Remove(random.Next(lines[i].Length), 1);
                break;
            }
            case 4:
                lines.InsertRange(random.Next(lines.Count + 1), Enumerable.Range(0, random.Next(1, 6)).Select(_ => Pick(random)));
                break;
            default:
                if (lines.Count > 0) lines[random.Next(lines.Count)] = Pick(random);
                break;
        }
    }

    private static void AssertSame(LineTokens[] expected, LineTokens[] actual, string context)
    {
        Assert.True(expected.Length == actual.Length, $"{context}: line count {expected.Length} != {actual.Length}");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i].Line == actual[i].Line, $"{context}: line index at {i}");
            Assert.True(expected[i].Tokens.SequenceEqual(actual[i].Tokens),
                $"{context}: tokens differ at line {i}: [{string.Join(", ", expected[i].Tokens)}] vs [{string.Join(", ", actual[i].Tokens)}]");
        }
    }
}
