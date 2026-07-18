using sk0ya.Loomo.App.Services;
using System.IO;

namespace sk0ya.Loomo.Tests;

public class EditorSupportResolverTests
{
    [Fact]
    public void Registered_provider_has_priority_over_fallbacks()
    {
        var provider = new TestProvider(".cs");
        var resolver = CreateResolver(provider);

        var selection = resolver.Resolve("source.cs");

        Assert.Equal(EditorSupportKind.Provider, selection.Kind);
        Assert.Same(provider, selection.Provider);
    }

    [Fact]
    public void Code_file_without_provider_uses_code_support()
    {
        var selection = CreateResolver().Resolve("source.cs");

        Assert.Equal(EditorSupportKind.Code, selection.Kind);
        Assert.Null(selection.Provider);
    }

    [Fact]
    public void Unknown_text_file_is_unsupported()
    {
        var selection = CreateResolver().Resolve("notes.unknown");

        Assert.Equal(EditorSupportKind.Unsupported, selection.Kind);
        Assert.Null(selection.Provider);
    }

    [Fact]
    public void Unknown_binary_file_uses_hex_provider()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 0, 2]);

            var selection = CreateResolver().Resolve(path);

            Assert.Equal(EditorSupportKind.Provider, selection.Kind);
            Assert.IsType<HexEditorSupport>(selection.Provider);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static EditorSupportResolver CreateResolver(params IEditorSupportProvider[] providers)
        => new(new EditorSupportRegistry(providers), new CodeEditorSupport(), new HexEditorSupport());

    private sealed class TestProvider(string extension) : IEditorSupportHtmlProvider
    {
        public IReadOnlyCollection<string> SupportedExtensions => [extension];
        public string DescribeTitle(string filePath) => "Test";
        public string RenderHtml(string filePath, string text) => text;
    }
}
