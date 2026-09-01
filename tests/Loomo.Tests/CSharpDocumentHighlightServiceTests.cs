using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Editor;
using sk0ya.Loomo.CSharp.Projects;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpDocumentHighlightServiceTests
{
    [Fact]
    public async Task Highlights_only_the_same_symbol_and_classifies_reads_and_writes()
    {
        const string path = "C:\\work\\Highlight.cs";
        const string source = """
            class Sample
            {
                private int value;
                void Set()
                {
                    value = 1;
                    var snapshot = value;
                    value++;
                }

                void Other()
                {
                    var value = 2;
                    _ = value;
                }
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(new Dictionary<string, string>
        {
            [path] = source,
        });
        var declarationOffset = source.IndexOf("value", StringComparison.Ordinal);

        var highlights = await CSharpDocumentHighlightService.FindAsync(
            path, Position(source, declarationOffset), compilation);

        Assert.Equal(4, highlights.Count);
        Assert.Equal(
            [
                source.IndexOf("value", StringComparison.Ordinal),
                source.IndexOf("value =", StringComparison.Ordinal),
                source.IndexOf("= value;", StringComparison.Ordinal) + 2,
                source.IndexOf("value++;", StringComparison.Ordinal),
            ],
            highlights.Select(highlight => Offset(source, highlight.Range.Start)).ToArray());
        Assert.Equal(DocumentHighlightKind.Write, highlights[0].Kind);
        Assert.Equal(DocumentHighlightKind.Write, highlights[1].Kind);
        Assert.Equal(DocumentHighlightKind.Read, highlights[2].Kind);
    }

    private static LspPosition Position(string source, int offset)
    {
        var prefix = source[..offset];
        return new(prefix.Count(character => character == '\n'),
            prefix[(prefix.LastIndexOf('\n') + 1)..].Length);
    }

    private static int Offset(string source, LspPosition position)
    {
        var lines = source.Split('\n');
        return lines.Take(position.Line).Sum(line => line.Length + 1) + position.Character;
    }
}
