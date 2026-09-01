using System.IO;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpSemanticTokenServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "LoomoCSharpSemanticTokens_" + Guid.NewGuid().ToString("N"));

    public CSharpSemanticTokenServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Returns_roslyn_symbol_categories_and_modifiers()
    {
        var source = """
            using System;
            [Obsolete]
            class Sample
            {
                private readonly int field;
                public int Property { get; }
                public void Run(string arg)
                {
                    int local = 0;
                    Console.WriteLine(arg);
                }
            }
            """;
        var path = Write("Sample.cs", source);

        var tokens = CSharpSemanticTokenService.Get(CreateSolution(path), path, source);

        AssertToken(tokens, source, "Sample", "class", "declaration", "deprecated");
        AssertToken(tokens, source, "Obsolete", "attribute");
        AssertToken(tokens, source, "field", "variable", "declaration", "readonly");
        AssertToken(tokens, source, "Property", "property", "declaration", "readonly");
        AssertToken(tokens, source, "Run", "method", "declaration");
        AssertToken(tokens, source, "arg", "parameter", "declaration");
        AssertToken(tokens, source, "local", "variable", "declaration");
        AssertToken(tokens, source, "Console", "class", "static", "defaultLibrary");
    }

    [Fact]
    public void Classifies_only_the_attribute_name_not_named_arguments()
    {
        const string source = "using System; [Obsolete(DiagnosticId = \"LEGACY\")] class Sample { }";
        var path = Write("AttributeArguments.cs", source);

        var tokens = CSharpSemanticTokenService.Get(CreateSolution(path), path, source);

        AssertToken(tokens, source, "Obsolete", "attribute");
        Assert.DoesNotContain(tokens, token =>
            token.StartChar == source.IndexOf("DiagnosticId", StringComparison.Ordinal) &&
            token.TokenType == "attribute");
    }

    [Fact]
    public async Task Async_provider_preserves_unsaved_active_text()
    {
        var path = Write("Unsaved.cs", "class OldName { }");
        const string source = "class NewName { void Run() { int value = 1; } }";

        var tokens = await CSharpSemanticTokenService.GetAsync(
            CreateSolution(path), path, source);

        AssertToken(tokens, source, "NewName", "class", "declaration");
        AssertToken(tokens, source, "Run", "method", "declaration");
        AssertToken(tokens, source, "value", "variable", "declaration");
        Assert.DoesNotContain(tokens, token =>
            token.StartChar == source.IndexOf("OldName", StringComparison.Ordinal));
    }

    [Fact]
    public void Returns_static_and_reassigned_variable_modifiers_like_roslyn_language_server()
    {
        const string source = "class Sample { static int Count; static void Update() { Count += 1; } }";
        var path = Write("Modifiers.cs", source);

        var tokens = CSharpSemanticTokenService.Get(CreateSolution(path), path, source);

        AssertTokenAt(tokens, source, source.IndexOf("Count", StringComparison.Ordinal),
            "variable", "declaration", "static");
        AssertTokenAt(tokens, source, source.LastIndexOf("Count", StringComparison.Ordinal),
            "variable", "static", "ReassignedVariable");
        AssertTokenAt(tokens, source, source.IndexOf("Update", StringComparison.Ordinal),
            "method", "declaration", "static");
    }

    [Fact]
    public void Marks_only_the_written_member_not_a_member_access_receiver()
    {
        const string source = """
            class Holder { public int Value; }
            class Sample
            {
                void Update(Holder holder, ref int value)
                {
                    holder.Value = value;
                    value++;
                }
            }
            """;
        var path = Write("WriteTargets.cs", source);

        var tokens = CSharpSemanticTokenService.Get(CreateSolution(path), path, source);

        var holderOffset = source.IndexOf("holder.Value", StringComparison.Ordinal);
        AssertTokenAt(tokens, source, holderOffset, "parameter");
        AssertTokenAt(tokens, source, holderOffset + "holder.".Length,
            "variable", "ReassignedVariable");
        AssertTokenAt(tokens, source, source.LastIndexOf("value", StringComparison.Ordinal),
            "parameter", "ReassignedVariable");
    }

    [Fact]
    public void Preserves_async_and_const_semantic_modifiers()
    {
        const string source = """
            using System.Threading.Tasks;
            class Sample
            {
                private const int Count = 1;
                private async Task RunAsync()
                {
                    await Task.CompletedTask;
                }
            }
            """;
        var path = Write("AsyncModifiers.cs", source);

        var tokens = CSharpSemanticTokenService.Get(CreateSolution(path), path, source);

        AssertToken(tokens, source, "Count", "variable", "declaration", "static", "readonly");
        AssertToken(tokens, source, "RunAsync", "method", "declaration", "async");
        AssertTokenAt(tokens, source, source.IndexOf("Task RunAsync", StringComparison.Ordinal),
            "class", "defaultLibrary");
    }

    [Fact]
    public void Preserves_readonly_struct_and_get_only_property_modifiers()
    {
        const string source = "readonly struct ReadonlyValue { public int ReadonlyProperty { get; } }";
        var path = Write("ReadonlySymbols.cs", source);

        var tokens = CSharpSemanticTokenService.Get(CreateSolution(path), path, source);

        AssertToken(tokens, source, "ReadonlyValue", "struct", "declaration", "readonly");
        AssertToken(tokens, source, "ReadonlyProperty", "property", "declaration", "readonly");
    }

    [Fact]
    public void Classifies_event_field_declarations_as_events()
    {
        const string source = "using System; class Sample { public event Action Changed; void Raise() => Changed?.Invoke(); }";
        var path = Write("Events.cs", source);

        var tokens = CSharpSemanticTokenService.Get(CreateSolution(path), path, source);

        AssertToken(tokens, source, "Changed", "event", "declaration");
        AssertTokenAt(tokens, source, source.LastIndexOf("Changed", StringComparison.Ordinal), "event");
    }

    [Fact]
    public void Classifies_modern_local_declarations_from_roslyn_declared_symbols()
    {
        const string source = """
            using System;
            class Sample
            {
                void Run(object input, (int First, string Second) pair)
                {
                    if (input is string text) Console.WriteLine(text);
                    foreach (var item in new[] { 1 }) Console.WriteLine(item);
                    try { throw new Exception(); }
                    catch (Exception error) { Console.WriteLine(error.Message); }
                    (var first, var second) = (1, "value");
                    Console.WriteLine(first + second);
                }
            }
            """;
        var path = Write("ModernLocals.cs", source);

        var tokens = CSharpSemanticTokenService.Get(CreateSolution(path), path, source);

        AssertToken(tokens, source, "text", "variable", "declaration");
        AssertToken(tokens, source, "item", "variable", "declaration");
        AssertToken(tokens, source, "error", "variable", "declaration");
        AssertToken(tokens, source, "first", "variable", "declaration");
        AssertToken(tokens, source, "second", "variable", "declaration");
    }

    private static void AssertToken(
        IReadOnlyList<SemanticToken> tokens,
        string source,
        string text,
        string type,
        params string[] modifiers)
    {
        var offset = source.IndexOf(text, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"テスト本文に '{text}' がありません。");
        var line = source[..offset].Count(character => character == '\n');
        var lineStart = source.LastIndexOf('\n', Math.Max(0, offset - 1));
        var column = offset - (lineStart < 0 ? 0 : lineStart + 1);
        var token = Assert.Single(tokens, candidate =>
            candidate.Line == line && candidate.StartChar == column &&
            candidate.Length == text.Length && candidate.TokenType == type);
        Assert.Equal(modifiers, token.Modifiers);
    }

    private static void AssertTokenAt(
        IReadOnlyList<SemanticToken> tokens,
        string source,
        int offset,
        string type,
        params string[] modifiers)
    {
        Assert.True(offset >= 0);
        var line = source[..offset].Count(character => character == '\n');
        var lineStart = source.LastIndexOf('\n', Math.Max(0, offset - 1));
        var column = offset - (lineStart < 0 ? 0 : lineStart + 1);
        var text = source[offset..].TakeWhile(char.IsLetterOrDigit).Count();
        var token = Assert.Single(tokens, candidate =>
            candidate.Line == line && candidate.StartChar == column &&
            candidate.Length == text && candidate.TokenType == type);
        Assert.Equal(modifiers, token.Modifiers);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private SolutionModel CreateSolution(string sourcePath)
    {
        var project = new ProjectModel("Sample", Path.Combine(_root, "Sample.csproj"), _root, [],
            [new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem(Path.GetFileName(sourcePath), sourcePath)], [], [], [])],
            "net10.0", false, ProjectLoadState.Ready);
        return new SolutionModel(null, "Sample", _root, [project], ProjectLoadState.Ready);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
