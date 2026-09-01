using Editor.Core.Lsp;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpIntroducePropertyServiceTests
{
    [Fact]
    public void Introduces_a_property_and_replaces_the_selected_expression()
    {
        const string source = """
            class Sample
            {
                private int _value;

                int Run()
                {
                    return _value + 1;
                }
            }
            """;
        var start = source.IndexOf("_value + 1", StringComparison.Ordinal);
        var result = CSharpIntroducePropertyService.Introduce(
            "C:\\work\\Sample.cs", source, Range(source, start, "_value + 1".Length),
            "NextValue", "int", "public");

        Assert.Null(result.Error);
        var edits = Assert.Single(result.Edit!.Changes.Values);
        Assert.Equal(2, edits.Count);
        Assert.Contains(edits, edit => edit.NewText == "NextValue");
        Assert.Contains(edits, edit => edit.NewText.Contains(
            "public int NextValue => _value + 1;", StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_a_property_that_would_capture_a_local_or_parameter()
    {
        const string source = """
            class Sample
            {
                int Run(int value)
                {
                    var local = value + 1;
                    return local;
                }
            }
            """;
        var start = source.LastIndexOf("local", StringComparison.Ordinal);
        var result = CSharpIntroducePropertyService.Introduce(
            "C:\\work\\Sample.cs", source, Range(source, start, "local".Length),
            "Current", "int");

        Assert.Null(result.Edit);
        Assert.Contains("ローカル変数", result.Error);
    }

    [Fact]
    public void Rejects_invalid_property_inputs_and_duplicate_members()
    {
        const string source = """
            class Sample
            {
                private int _value;

                int Run() => _value;
            }
            """;
        var start = source.LastIndexOf("_value", StringComparison.Ordinal);
        var selection = Range(source, start, "_value".Length);

        var invalidName = CSharpIntroducePropertyService.Introduce(
            "C:\\work\\Sample.cs", source, selection, "class", "int");
        Assert.Null(invalidName.Edit);

        var duplicate = CSharpIntroducePropertyService.Introduce(
            "C:\\work\\Sample.cs", source, selection, "_value", "int");
        Assert.Null(duplicate.Edit);
        Assert.Contains("同名", duplicate.Error);
    }

    private static LspRange Range(string source, int start, int length)
    {
        static LspPosition Position(string value, int offset)
        {
            var lines = value[..offset].Split('\n');
            return new LspPosition(lines.Length - 1, lines[^1].Length);
        }

        return new LspRange(Position(source, start), Position(source, start + length));
    }
}
