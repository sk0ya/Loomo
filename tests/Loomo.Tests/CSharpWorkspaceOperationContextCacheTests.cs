using System;
using System.IO;
using System.Linq;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

/// <summary>Compilation キャッシュを直列化する（静的な共有資源なので、他の測定と混ぜない）。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CSharpCompilationCacheCollection
{
    public const string Name = "C# compilation cache";
}

/// <summary>
/// 打鍵ごとにソリューション全体を読み直してパースし直す——これが「入力が途中で固まる」の正体だった
/// （UI スレッドで 1 回 1.3〜3 秒）。キャッシュは体感を決めているので、
/// 「効いていること」を実測ではなく<b>構造</b>で固定する：同じ入力なら同じ Compilation、
/// 1 文字の編集なら<b>その 1 ファイルの構文木だけ</b>が新しくなる。
/// </summary>
[Collection(CSharpCompilationCacheCollection.Name)]
public sealed class CSharpWorkspaceOperationContextCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "loomo-compcache-" + Guid.NewGuid().ToString("N")[..8]);

    public CSharpWorkspaceOperationContextCacheTests()
    {
        Directory.CreateDirectory(_root);
        CSharpWorkspaceOperationContext.ClearCompilationCacheForTest();
    }

    public void Dispose()
    {
        CSharpWorkspaceOperationContext.ClearCompilationCacheForTest();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Returns_the_same_compilation_when_nothing_changed()
    {
        var active = Write("Active.cs", "class Active { void M() { } }");
        Write("Other.cs", "class Other { }");
        var solution = CreateSolution("Active.cs", "Other.cs");
        var source = File.ReadAllText(active);

        var first = Create(solution, active, source);
        var second = Create(solution, active, source);

        Assert.Same(first, second);
    }

    [Fact]
    public void Reparses_only_the_edited_file()
    {
        var active = Write("Active.cs", "class Active { void M() { } }");
        var other = Write("Other.cs", "class Other { }");
        var third = Write("Third.cs", "class Third { }");
        var solution = CreateSolution("Active.cs", "Other.cs", "Third.cs");

        var before = Create(solution, active, File.ReadAllText(active));
        var after = Create(solution, active, "class Active { void M() { int x; } }");

        Assert.NotSame(before, after);
        Assert.NotSame(TreeFor(before, active), TreeFor(after, active));
        // 触っていないファイルの構文木は<b>同じインスタンス</b>のまま＝パースし直していない。
        Assert.Same(TreeFor(before, other), TreeFor(after, other));
        Assert.Same(TreeFor(before, third), TreeFor(after, third));
        // 差し替えた木が Compilation に反映されていること（キャッシュが古い本文を配らない）。
        Assert.Contains("int x;", TreeFor(after, active).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuilds_when_the_set_of_files_changed()
    {
        // 差分更新が追えるのは「同じ顔ぶれの本文が変わった」ときだけ。ファイルが消えた／増えたら
        // 作り直す——古い顔ぶれのまま配ると、消えたファイルの型がいつまでも解決してしまう。
        var active = Write("Active.cs", "class Active { }");
        var other = Write("Other.cs", "class Other { }");
        var solution = CreateSolution("Active.cs", "Other.cs");
        var source = File.ReadAllText(active);

        var before = Create(solution, active, source);
        Assert.Contains(before.SyntaxTrees, tree => SamePath(tree.FilePath, other));

        File.Delete(other);
        var after = Create(solution, active, source);

        Assert.NotSame(before, after);
        Assert.DoesNotContain(after.SyntaxTrees, tree => SamePath(tree.FilePath, other));
    }

    private static bool SamePath(string? left, string right)
        => !string.IsNullOrEmpty(left) &&
           string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
               StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Keeps_separate_entries_for_callers_that_ask_for_different_scopes()
    {
        // 同じ打鍵で、補完は Solution・意味色付けは ProjectGraph でここへ来る。枠が 1 つだと
        // 互いを追い出し、キャッシュがあるのに毎回作り直すという最悪の形になっていた。
        var active = Write("Active.cs", "class Active { }");
        var solution = CreateSolution("Active.cs");
        var source = File.ReadAllText(active);

        var wide = Create(solution, active, source, CSharpWorkspaceSourceScope.Solution);
        var narrow = Create(solution, active, source, CSharpWorkspaceSourceScope.ProjectGraph);
        var wideAgain = Create(solution, active, source, CSharpWorkspaceSourceScope.Solution);

        Assert.NotSame(wide, narrow);
        Assert.Same(wide, wideAgain);
    }

    private static Microsoft.CodeAnalysis.SyntaxTree TreeFor(
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation, string path)
        => compilation.SyntaxTrees.Single(tree =>
            string.Equals(Path.GetFullPath(tree.FilePath ?? ""), Path.GetFullPath(path),
                StringComparison.OrdinalIgnoreCase));

    private static Microsoft.CodeAnalysis.CSharp.CSharpCompilation Create(
        SolutionModel solution, string activePath, string activeText,
        CSharpWorkspaceSourceScope scope = CSharpWorkspaceSourceScope.Solution)
    {
        var context = CSharpWorkspaceOperationContext.Create(
            solution, activePath, activeText, scope, includeSemanticCompilation: true);
        Assert.NotNull(context.SemanticCompilation);
        return context.SemanticCompilation!;
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private SolutionModel CreateSolution(params string[] names)
    {
        var project = new ProjectModel("Sample", Path.Combine(_root, "Sample.csproj"), _root, [],
            [new TargetFrameworkModel("net10.0", [], "latest",
                names.Select(name => new ProjectItem(name, Path.Combine(_root, name))).ToArray(),
                [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        return new SolutionModel(null, "Sample", _root, [project], ProjectLoadState.Ready);
    }
}
