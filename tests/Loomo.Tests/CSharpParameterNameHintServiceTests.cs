using System.Linq;
using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpParameterNameHintServiceTests
{
    [Fact]
    public void Resolves_parameter_names_for_invocations_and_object_creation()
    {
        const string source = """
            class Pair
            {
                public Pair(int first, string second) { }
            }

            class Calculator
            {
                int Add(int left, string right) => left;

                void Use()
                {
                    Add(1, "value");
                    var pair = new Pair(2, "text");
                }
            }
            """;

        var hints = CSharpParameterNameHintService.Get(
            null, "C:\\work\\Calculator.cs", source, 0, int.MaxValue);

        Assert.Equal(["left:", "right:", "first:", "second:"],
            hints.Select(hint => hint.Label).ToArray());
        Assert.All(hints, hint => Assert.Equal(Editor.Core.Lsp.InlayHintKind.Parameter, hint.Kind));
    }

    [Fact]
    public void Does_not_duplicate_explicit_named_arguments_and_honors_line_range()
    {
        const string source = """
            class Calculator
            {
                int Add(int left, string right) => left;

                void Use()
                {
                    Add(right: "value", left: 1);
                    Add(2, "other");
                }
            }
            """;

        var secondCallLine = source[..source.LastIndexOf("Add(2", StringComparison.Ordinal)]
            .Count(c => c == '\n');
        var hints = CSharpParameterNameHintService.Get(
            null, "C:\\work\\Calculator.cs", source, secondCallLine, secondCallLine);

        Assert.Equal(["left:", "right:"], hints.Select(hint => hint.Label).ToArray());
        Assert.All(hints, hint => Assert.Equal(secondCallLine, hint.Position.Line));
    }
}
