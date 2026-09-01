using System.Linq;
using sk0ya.Loomo.CSharp.Editor;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpCompletionServiceTests
{
    [Fact]
    public async Task Returns_semantic_member_candidates_with_a_replace_edit()
    {
        const string path = "C:\\work\\Completion.cs";
        const string source = """
            class Service
            {
                public int Read() => 1;
                public int Reset() => 2;

                int Use()
                {
                    var service = new Service();
                    return service.Re;
                }
            }
            """;
        var completionOffset = source.IndexOf("service.Re", StringComparison.Ordinal) + "service.Re".Length;
        var (line, character) = LinePosition(source, completionOffset);

        var items = await CSharpCompletionService.GetAsync(
            null, path, source, line, character);
        Assert.True(items.Count > 0,
            $"候補なし: {string.Join(",", items.Select(item => item.Label))}");
        var read = Assert.Single(items, item => item.Label == "Read");

        Assert.Equal(Editor.Core.Lsp.CompletionItemKind.Method, read.Kind);
        Assert.Equal("Read", read.InsertText);
        Assert.NotNull(read.TextEdit);
        Assert.Equal("Re", source[(completionOffset - 2)..completionOffset]);
    }

    [Fact]
    public async Task Preserves_roslyn_import_edits_when_a_type_is_not_imported()
    {
        const string path = "C:\\work\\CompletionImports.cs";
        const string source = "class C { void M() { var builder = new StringB; } }";
        var completionOffset = source.IndexOf("StringB", StringComparison.Ordinal) + "StringB".Length;
        var (line, character) = LinePosition(source, completionOffset);

        var items = await CSharpCompletionService.GetAsync(
            null, path, source, line, character);
        var builder = items.FirstOrDefault(item => item.Label == "StringBuilder");
        Assert.NotNull(builder);
        Assert.Contains(builder!.AdditionalTextEdits ?? [], edit =>
            edit.NewText.Contains("using System.Text;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Carries_roslyn_xml_documentation_into_completion_details()
    {
        const string path = "C:\\work\\CompletionDocumentation.cs";
        const string source = """
            class Service
            {
                /// <summary>Reads the current value.</summary>
                public int Read() => 1;
            }

            class Caller
            {
                int Use()
                {
                    var service = new Service();
                    return service.Re;
                }
            }
            """;
        var completionOffset = source.IndexOf("service.Re", StringComparison.Ordinal)
            + "service.Re".Length;
        var (line, character) = LinePosition(source, completionOffset);

        var items = await CSharpCompletionService.GetAsync(
            null, path, source, line, character);
        var read = Assert.Single(items, item => item.Label == "Read");

        Assert.Contains("Reads the current value", read.Documentation,
            StringComparison.Ordinal);
    }

    private static (int Line, int Character) LinePosition(string source, int offset)
    {
        var line = source[..offset].Count(c => c == '\n');
        var lineStart = source.LastIndexOf('\n', Math.Max(0, offset - 1));
        return (line, offset - (lineStart < 0 ? 0 : lineStart + 1));
    }
}
