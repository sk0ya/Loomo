using System.Linq;
using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpSignatureHelpServiceTests
{
    [Fact]
    public void Resolves_the_active_parameter_and_documentation_from_Roslyn()
    {
        const string source = """
            class Calculator
            {
                /// <summary>Adds two values.</summary>
                int Add(int left, string right) => left;

                void Use()
                {
                    Add(1, "value");
                }
            }
            """;
        var position = PositionOf(source, "Add(1, \"value\")") + "Add(1, \"value\"".Length;
        var (line, character) = LinePositionOf(source, position);

        var result = CSharpSignatureHelpService.Get(
            null, "C:\\work\\Calculator.cs", source, line, character);

        var signature = Assert.Single(result!.Signatures);
        Assert.Contains("Add", signature.Label);
        Assert.Equal(1, result.ActiveParameter);
        Assert.Contains("Adds two values", signature.Documentation);
        Assert.Equal(2, signature.Parameters.Count);
    }

    [Fact]
    public void Preserves_overload_candidates_at_an_unresolved_call()
    {
        const string source = """
            class Calculator
            {
                int Add(int left, int right) => left + right;
                string Add(string left, string right) => left + right;

                void Use()
                {
                    Add(unknown, unknown);
                }
            }
            """;
        var position = PositionOf(source, "Add(unknown, unknown)") + "Add(unknown, unknown".Length;
        var (line, character) = LinePositionOf(source, position);

        var result = CSharpSignatureHelpService.Get(
            null, "C:\\work\\Calculator.cs", source, line, character);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Signatures.Count);
        Assert.All(result.Signatures, signature =>
            Assert.Equal(2, signature.Parameters.Count));
    }

    private static int PositionOf(string source, string value)
        => source.IndexOf(value, StringComparison.Ordinal);

    private static (int Line, int Character) LinePositionOf(string source, int offset)
    {
        var line = source[..offset].Count(c => c == '\n');
        var lineStart = source.LastIndexOf('\n', Math.Max(0, offset - 1));
        return (line, offset - (lineStart < 0 ? 0 : lineStart + 1));
    }
}
