using sk0ya.Loomo.App.Services;
using System.IO;

namespace sk0ya.Loomo.Tests;

public class EditorSupportPipelineTests
{
    [Fact]
    public async Task Matching_page_key_prepares_incremental_body()
    {
        var provider = new IncrementalProvider();
        var pipeline = new EditorSupportPipeline();

        var result = await pipeline.PrepareAsync(provider, Context(readyPageKey: "page"));

        Assert.Null(result.Html);
        Assert.Equal("body:content", result.Body);
        Assert.Equal("page", result.PageKey);
        Assert.Equal(0, provider.FullRenderCount);
    }

    [Fact]
    public async Task Different_page_key_prepares_complete_document()
    {
        var provider = new IncrementalProvider();
        var pipeline = new EditorSupportPipeline();

        var result = await pipeline.PrepareAsync(provider, Context(readyPageKey: "old"));

        Assert.Equal("html:content", result.Html);
        Assert.Null(result.Body);
        Assert.Equal(1, provider.FullRenderCount);
    }

    [Fact]
    public async Task Provider_exception_becomes_error_page()
    {
        var pipeline = new EditorSupportPipeline();

        var result = await pipeline.PrepareAsync(new ThrowingProvider(), Context());

        Assert.Contains("プレビューエラー", result.Html);
        Assert.Contains("conversion failed", result.Html);
        Assert.Null(result.PageKey);
    }

    [Fact]
    public async Task Provider_that_reads_file_does_not_receive_editor_text()
    {
        var provider = new FileBackedProvider();

        await new EditorSupportPipeline().PrepareAsync(provider, Context());

        Assert.Equal(string.Empty, provider.ReceivedText);
    }

    private static EditorSupportContext Context(string? readyPageKey = null) => new(
        FilePath: Path.Combine("workspace", "document.test"),
        Text: "content",
        WorkspaceRoot: "workspace",
        ReadyPageKey: readyPageKey,
        PreviewTheme: "dark");

    private sealed class IncrementalProvider : IEditorSupportIncrementalHtmlProvider
    {
        public IReadOnlyCollection<string> SupportedExtensions => [".test"];
        public int FullRenderCount { get; private set; }
        public string DescribeTitle(string filePath) => "Test";
        public string PageContextKey(string filePath, string text) => "page";
        public string RenderBody(string filePath, string text) => $"body:{text}";
        public string RenderHtml(string filePath, string text)
        {
            FullRenderCount++;
            return $"html:{text}";
        }
    }

    private sealed class ThrowingProvider : IEditorSupportHtmlProvider
    {
        public IReadOnlyCollection<string> SupportedExtensions => [".test"];
        public string DescribeTitle(string filePath) => "Broken";
        public string RenderHtml(string filePath, string text)
            => throw new InvalidOperationException("conversion failed");
    }

    private sealed class FileBackedProvider : IEditorSupportHtmlProvider
    {
        public IReadOnlyCollection<string> SupportedExtensions => [".test"];
        public bool UsesEditorText => false;
        public string? ReceivedText { get; private set; }
        public string DescribeTitle(string filePath) => "File";
        public string RenderHtml(string filePath, string text)
        {
            ReceivedText = text;
            return "html";
        }
    }
}
