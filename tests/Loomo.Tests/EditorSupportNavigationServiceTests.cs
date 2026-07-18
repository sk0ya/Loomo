using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public class EditorSupportNavigationServiceTests
{
    [Theory]
    [InlineData("https://page.loomo/preview.html?v=1", true)]
    [InlineData("https://example.com/preview.html", false)]
    [InlineData(null, false)]
    public void Preview_URL_is_identified_by_virtual_host(string? url, bool expected)
        => Assert.Equal(expected, EditorSupportNavigationService.IsPreviewUrl(url));

    [Fact]
    public void Writing_page_creates_file_and_unique_navigation_versions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Loomo.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new EditorSupportNavigationService(folder);
            Assert.True(service.TryWritePage("<p>first</p>", out var first));
            Assert.True(service.TryWritePage("<p>second</p>", out var second));
            Assert.NotEqual(first, second);
            Assert.Equal("<p>second</p>", File.ReadAllText(Path.Combine(folder, "preview.html")));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
