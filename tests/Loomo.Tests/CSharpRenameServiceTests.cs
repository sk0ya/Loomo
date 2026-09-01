using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpRenameServiceTests
{
    [Fact]
    public async Task Renames_declaration_and_semantic_references_across_compile_files()
    {
        const string sourcePath = "C:\\work\\Sample.cs";
        const string consumerPath = "C:\\work\\Consumer.cs";
        const string source = "class Sample { private int _value; public int Get() => _value; }";
        const string consumer = "class Consumer { int Run(Sample sample) => sample.Get(); }";
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [sourcePath] = source,
            [consumerPath] = consumer,
        });
        var position = Position(source, source.IndexOf("_value", StringComparison.Ordinal));

        var result = await CSharpRenameService.RenameAsync(
            sourcePath, source, position, "_count", compilation);

        Assert.Null(result.Error);
        Assert.Equal("_value", result.SymbolName);
        var edits = result.Edit!.Changes;
        Assert.Contains(LspUri.FromPath(sourcePath), edits.Keys);
        var sourceEdits = edits[LspUri.FromPath(sourcePath)];
        Assert.Equal(2, sourceEdits.Count);
        Assert.All(sourceEdits, edit => Assert.Equal("_count", edit.NewText));
        Assert.DoesNotContain(LspUri.FromPath(consumerPath), edits.Keys);
    }

    [Fact]
    public async Task Renames_method_calls_in_another_compile_file_without_touching_same_named_symbols()
    {
        const string sourcePath = "C:\\work\\Service.cs";
        const string consumerPath = "C:\\work\\Caller.cs";
        const string source = "class Service { public int Read() => 1; }";
        const string consumer = "class Caller { int Run(Service service) => service.Read(); int Read() => 2; }";
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [sourcePath] = source,
            [consumerPath] = consumer,
        });

        var result = await CSharpRenameService.RenameAsync(
            sourcePath, source,
            Position(source, source.IndexOf("Read", StringComparison.Ordinal)),
            "Fetch", compilation);

        Assert.Null(result.Error);
        Assert.Equal("Read", result.SymbolName);
        Assert.Contains(result.Edit!.Changes[LspUri.FromPath(sourcePath)],
            edit => edit.NewText == "Fetch");
        var callerEdits = result.Edit.Changes[LspUri.FromPath(consumerPath)];
        Assert.Single(callerEdits);
        Assert.Equal("Fetch", callerEdits[0].NewText);
    }

    [Fact]
    public async Task Does_not_rename_an_unrelated_same_named_local()
    {
        const string path = "C:\\work\\Locals.cs";
        const string source = "class Sample { int value; int Run() { var value = 1; return value; } int Get() => value; }";
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string> { [path] = source });
        var position = Position(source, source.IndexOf("int value;", StringComparison.Ordinal) + 4);

        var result = await CSharpRenameService.RenameAsync(
            path, source, position, "count", compilation);

        Assert.Null(result.Error);
        var edits = result.Edit!.Changes[LspUri.FromPath(path)];
        Assert.Equal(2, edits.Count);
        Assert.All(edits, edit => Assert.Equal("count", edit.NewText));
        Assert.Equal(2, edits.Count(edit => edit.Range.Start.Line == 0));
    }

    [Fact]
    public async Task Renames_override_contract_and_calls_through_interface_and_base_types()
    {
        const string contractPath = "C:\\work\\IRunner.cs";
        const string basePath = "C:\\work\\Base.cs";
        const string derivedPath = "C:\\work\\Derived.cs";
        const string callerPath = "C:\\work\\Caller.cs";
        const string contract = "public interface IRunner { void Run(int value); }";
        const string baseType = "public class Base : IRunner { public virtual void Run(int value) { } }";
        const string derived = "public class Derived : Base { public override void Run(int value) { } }";
        const string caller = """
            public class Caller
            {
                void Use(IRunner contract, Base baseValue, Derived derived)
                {
                    contract.Run(1);
                    baseValue.Run(2);
                    derived.Run(3);
                }
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [contractPath] = contract,
            [basePath] = baseType,
            [derivedPath] = derived,
            [callerPath] = caller,
        });

        var result = await CSharpRenameService.RenameAsync(
            derivedPath, derived,
            Position(derived, derived.IndexOf("Run", StringComparison.Ordinal)),
            "Execute", compilation);

        Assert.Null(result.Error);
        Assert.Equal(4, result.Edit!.Changes.Count);
        Assert.All(result.Edit.Changes.Values.SelectMany(edits => edits),
            edit => Assert.Equal("Execute", edit.NewText));
        Assert.Equal(4, result.Edit.ExpectedTexts!.Count);
        Assert.Equal(derived, result.Edit.ExpectedTexts[System.IO.Path.GetFullPath(derivedPath)]);
    }

    [Theory]
    [InlineData("class", "新しい名前がC#の識別子として正しくありません")]
    [InlineData("", "新しい名前がC#の識別子として正しくありません")]
    public async Task Rejects_invalid_new_names(string newName, string expected)
    {
        const string path = "C:\\work\\InvalidRename.cs";
        const string source = "class Sample { int Value; }";
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string> { [path] = source });

        var result = await CSharpRenameService.RenameAsync(
            path, source, new LspPosition(0, 6), newName, compilation);

        Assert.Null(result.Edit);
        Assert.Contains(expected, result.Error);
    }

    private static LspPosition Position(string source, int offset)
    {
        var prefix = source[..offset];
        return new LspPosition(prefix.Count(c => c == '\n'),
            prefix[(prefix.LastIndexOf('\n') + 1)..].Length);
    }
}
