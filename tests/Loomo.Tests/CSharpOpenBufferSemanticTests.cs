using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpOpenBufferSemanticTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "LoomoCSharpOpenBuffer_" + Guid.NewGuid().ToString("N"));

    public CSharpOpenBufferSemanticTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Signature_hints_and_hover_use_an_unsaved_referenced_buffer()
    {
        var servicePath = Write("Service.cs",
            "public class Service { public int Read(int oldName) => oldName; }");
        var callerPath = Write("Caller.cs",
            "class Caller { int Run(Service service) => service.Read(1); }");
        var caller = File.ReadAllText(callerPath);
        var unsavedService = """
            public class Service
            {
                /// <summary>未保存バッファの説明。</summary>
                public int Read(int currentName) => currentName;
            }
            """;
        var solution = CreateSolution(servicePath, callerPath);
        var openTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [servicePath] = unsavedService,
        };
        var readOffset = caller.LastIndexOf("Read", StringComparison.Ordinal);
        var (line, character) = LinePosition(caller, readOffset + 6);

        var signature = CSharpSignatureHelpService.Get(
            solution, callerPath, caller, line, character, openTexts);
        var hints = CSharpParameterNameHintService.Get(
            solution, callerPath, caller, line, line, openTexts);
        var hover = CSharpHoverService.Get(
            solution, callerPath, caller, line,
            LinePosition(caller, readOffset).Character, openTexts);

        Assert.NotNull(signature);
        Assert.Contains("currentName", signature!.Signatures[0].Label, StringComparison.Ordinal);
        Assert.Contains("currentName:", hints.Select(hint => hint.Label));
        Assert.Contains("Service.Read(int currentName)", hover, StringComparison.Ordinal);
        Assert.Contains("未保存バッファの説明。", hover, StringComparison.Ordinal);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private SolutionModel CreateSolution(string servicePath, string callerPath)
    {
        var projectPath = Path.Combine(_root, "App.csproj");
        var target = new TargetFrameworkModel("net10.0", [], "latest", [
            new ProjectItem("Service.cs", servicePath),
            new ProjectItem("Caller.cs", callerPath),
        ], [], [], []);
        var project = new ProjectModel("App", projectPath, _root, [], [target],
            "net10.0", false, ProjectLoadState.Ready);
        return new SolutionModel(null, "App", _root, [project], ProjectLoadState.Ready);
    }

    private static (int Line, int Character) LinePosition(string source, int offset)
    {
        var line = source[..offset].Count(c => c == '\n');
        var lineStart = source.LastIndexOf('\n', Math.Max(0, offset - 1));
        return (line, offset - (lineStart < 0 ? 0 : lineStart + 1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
