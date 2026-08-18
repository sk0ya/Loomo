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
            var service = new EditorSupportNavigationService(folder, "preview-1.html");
            Assert.True(service.TryWritePage("<p>first</p>", out var first));
            Assert.True(service.TryWritePage("<p>second</p>", out var second));
            Assert.NotEqual(first, second);
            Assert.Equal("<p>second</p>", File.ReadAllText(Path.Combine(folder, "preview-1.html")));
            Assert.StartsWith("https://page.loomo/preview-1.html?v=", second);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>一時ページのフォルダーはプロファイル配下＝Loomo を2つ起動すると共有される。
    /// 同じ名前に書くと互いの本文を上書きし合うので、インスタンスごとに別ファイルであること。</summary>
    [Fact]
    public void Two_instances_write_to_separate_pages()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Loomo.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new EditorSupportNavigationService(folder, "preview-1.html");
            var second = new EditorSupportNavigationService(folder, "preview-2.html");
            Assert.True(first.TryWritePage("<p>first</p>", out _));
            Assert.True(second.TryWritePage("<p>second</p>", out _));
            Assert.Equal("<p>first</p>", File.ReadAllText(Path.Combine(folder, "preview-1.html")));
            Assert.Equal("<p>second</p>", File.ReadAllText(Path.Combine(folder, "preview-2.html")));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>掃除は「古い他インスタンスの置き土産」だけ——自分のページと、まだ新しいものは残す。</summary>
    [Fact]
    public void Cleaning_removes_only_stale_pages_of_other_instances()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Loomo.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new EditorSupportNavigationService(folder, "preview-1.html");
            Assert.True(service.TryWritePage("<p>mine</p>", out _));
            var stale = Path.Combine(folder, "preview-2.html");
            var fresh = Path.Combine(folder, "preview-3.html");
            File.WriteAllText(stale, "<p>stale</p>");
            File.WriteAllText(fresh, "<p>fresh</p>");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

            service.CleanStalePages(TimeSpan.FromDays(1));

            Assert.True(File.Exists(Path.Combine(folder, "preview-1.html")));
            Assert.True(File.Exists(fresh));
            Assert.False(File.Exists(stale));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
