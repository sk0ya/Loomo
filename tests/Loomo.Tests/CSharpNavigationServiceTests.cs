using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpNavigationServiceTests
{
    [Fact]
    public async Task Finds_a_source_definition_from_another_compile_file()
    {
        const string servicePath = "C:\\work\\Service.cs";
        const string callerPath = "C:\\work\\Caller.cs";
        const string service = "class Service { public int Read() => 1; }";
        const string caller = "class Caller { int Run(Service service) => service.Read(); }";
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [servicePath] = service,
            [callerPath] = caller,
        });

        var result = await CSharpNavigationService.FindDefinitionAsync(
            callerPath, caller,
            Position(caller, caller.LastIndexOf("Read", StringComparison.Ordinal)),
            compilation);

        Assert.Null(result.Error);
        Assert.Equal("Read", result.SymbolName);
        Assert.Equal(LspUri.FromPath(servicePath), result.Location!.Uri);
        Assert.Equal(0, result.Location.Range.Start.Line);
        Assert.Equal(service.IndexOf("Read", StringComparison.Ordinal),
            result.Location.Range.Start.Character);
    }

    [Fact]
    public async Task Finds_semantic_references_and_ignores_an_unrelated_same_named_method()
    {
        const string servicePath = "C:\\work\\Service.cs";
        const string callerPath = "C:\\work\\Caller.cs";
        const string service = "class Service { public int Read() => 1; }";
        const string caller = "class Caller { int Run(Service service) => service.Read(); int Read() => 2; }";
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [servicePath] = service,
            [callerPath] = caller,
        });

        var result = await CSharpNavigationService.FindReferencesAsync(
            servicePath, service,
            Position(service, service.IndexOf("Read", StringComparison.Ordinal)),
            compilation);

        Assert.Null(result.Error);
        Assert.Equal("Read", result.SymbolName);
        Assert.Equal(2, result.Locations.Count);
        Assert.Contains(result.Locations, location =>
            location.Uri == LspUri.FromPath(servicePath) &&
            location.Range.Start.Character == service.IndexOf("Read", StringComparison.Ordinal));
        Assert.Contains(result.Locations, location =>
            location.Uri == LspUri.FromPath(callerPath) &&
            location.Range.Start.Character == caller.IndexOf("Read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Finds_implementations_type_definition_and_declaration_by_symbol_identity()
    {
        const string contractPath = "C:\\work\\IValueProvider.cs";
        const string implementationPath = "C:\\work\\ValueProvider.cs";
        const string callerPath = "C:\\work\\Caller.cs";
        const string contract = "interface IValueProvider { int GetValue(); }";
        const string implementation = "class ValueProvider : IValueProvider { public int GetValue() => 1; }";
        const string caller = "class Caller { int Run(IValueProvider provider) => provider.GetValue(); }";
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [contractPath] = contract,
            [implementationPath] = implementation,
            [callerPath] = caller,
        });

        var interfaceMethodOffset = contract.IndexOf("GetValue", StringComparison.Ordinal);
        var implementations = await CSharpNavigationService.FindImplementationsAsync(
            contractPath, contract, Position(contract, interfaceMethodOffset), compilation);
        var typeDefinition = await CSharpNavigationService.FindTypeDefinitionAsync(
            callerPath, caller,
            Position(caller, caller.LastIndexOf("GetValue", StringComparison.Ordinal)), compilation);
        var declaration = await CSharpNavigationService.FindDeclarationAsync(
            callerPath, caller,
            Position(caller, caller.LastIndexOf("GetValue", StringComparison.Ordinal)), compilation);

        Assert.Null(implementations.Error);
        Assert.Contains(implementations.Locations, location =>
            location.Uri == LspUri.FromPath(implementationPath) &&
            location.Range.Start.Character == implementation.IndexOf("GetValue", StringComparison.Ordinal));
        Assert.Null(typeDefinition.Error);
        Assert.Contains(typeDefinition.Locations, location =>
            location.Uri == LspUri.FromPath(contractPath));
        Assert.Null(declaration.Error);
        Assert.Contains(declaration.Locations, location =>
            location.Uri == LspUri.FromPath(contractPath) &&
            location.Range.Start.Character == contract.IndexOf("GetValue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepares_rename_only_for_a_resolved_identifier()
    {
        const string path = "C:\\work\\RenameRange.cs";
        const string source = "class Sample { int value; void Set() { value = 1; } }";
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [path] = source,
        });
        var declarationOffset = source.IndexOf("value", StringComparison.Ordinal);

        var range = await CSharpRenameService.PrepareAsync(
            path, Position(source, declarationOffset), compilation);

        Assert.NotNull(range);
        Assert.Equal(declarationOffset, range!.Start.Character);
        Assert.Equal(declarationOffset + "value".Length, range.End.Character);
        Assert.Null(await CSharpRenameService.PrepareAsync(
            path, Position(source, source.IndexOf("class", StringComparison.Ordinal)), compilation));
    }

    private static LspPosition Position(string source, int offset)
    {
        var prefix = source[..offset];
        return new LspPosition(prefix.Count(character => character == '\n'),
            prefix[(prefix.LastIndexOf('\n') + 1)..].Length);
    }
}
