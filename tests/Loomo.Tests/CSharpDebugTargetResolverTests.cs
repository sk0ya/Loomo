using System.IO;
using sk0ya.Loomo.CSharp.Debug;

namespace sk0ya.Loomo.Tests;

/// <summary>CSharp DLLに移した.NETデバッグ対象探索の検証。</summary>
public sealed class CSharpDebugTargetResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"loomo-csharp-debug-{Guid.NewGuid():N}");

    public CSharpDebugTargetResolverTests()
        => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 一時フォルダーの後始末失敗はテスト結果に影響させない */ }
    }

    [Fact]
    public void FindBuildTarget_prefers_solution_before_project()
    {
        var project = Path.Combine(_root, "App.csproj");
        var solution = Path.Combine(_root, "App.slnx");
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(solution, "<Solution />");

        Assert.Equal(Path.GetFullPath(solution), CSharpDebugTargetResolver.FindBuildTarget(new[] { _root }));
    }

    [Fact]
    public void FindProject_skips_build_directories_and_finds_nested_project()
    {
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        File.WriteAllText(Path.Combine(_root, "obj", "Ignored.csproj"), "<Project />");
        var project = Path.Combine(_root, "src", "App", "App.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project />");

        Assert.Equal(Path.GetFullPath(project), CSharpDebugTargetResolver.FindProject(_root));
    }

    [Fact]
    public void FindProjectNear_and_FindOutputDll_resolve_from_an_output_path()
    {
        var project = Path.Combine(_root, "App", "App.csproj");
        var output = Path.Combine(_root, "App", "bin", "Debug", "net10.0", "App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(output, "dll");

        Assert.Equal(Path.GetFullPath(project), CSharpDebugTargetResolver.FindProjectNear(output));
        Assert.Equal(Path.GetFullPath(output), CSharpDebugTargetResolver.FindOutputDll(project));
    }

    [Fact]
    public void FindOutputDll_does_not_cross_selected_target_framework_directories()
    {
        var project = Path.Combine(_root, "App", "App.csproj");
        var net8 = Path.Combine(_root, "App", "bin", "Debug", "net8.0", "App.dll");
        var net10 = Path.Combine(_root, "App", "bin", "Debug", "net10.0", "App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(net8)!);
        Directory.CreateDirectory(Path.GetDirectoryName(net10)!);
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(net8, "net8");
        File.WriteAllText(net10, "net10");

        Assert.Equal(Path.GetFullPath(net8),
            CSharpDebugTargetResolver.FindOutputDll(project, "Debug", "net8.0"));
    }
}
