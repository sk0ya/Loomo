using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class StartupArgumentsTests
{
    private static DirectoryInfo NewFolder() => Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), $"loomo-startup-{Guid.NewGuid():N}"));

    [Fact]
    public void Accepts_a_folder_as_a_positional_argument()
    {
        var folder = NewFolder();

        var actual = StartupArguments.TryGetWorkspaceFolder([folder.FullName]);

        Assert.Equal(Path.GetFullPath(folder.FullName), actual);
    }

    [Fact]
    public void Accepts_the_explicit_workspace_option_and_ignores_files()
    {
        var folder = NewFolder();
        var file = Path.Combine(folder.FullName, "file.txt");
        File.WriteAllText(file, "test");

        var actual = StartupArguments.TryGetWorkspaceFolder(["--workspace", file, folder.FullName]);

        Assert.Equal(Path.GetFullPath(folder.FullName), actual);
    }

    [Fact]
    public void A_positional_existing_file_is_a_file_to_open_not_a_workspace()
    {
        var folder = NewFolder();
        var file = Path.Combine(folder.FullName, "note.md");
        File.WriteAllText(file, "# x");

        var request = StartupArguments.Parse([file]);

        Assert.Null(request.WorkspaceFolder);
        Assert.Equal([Path.GetFullPath(file)], request.Files);
        Assert.Empty(request.Urls);
    }

    [Fact]
    public void Web_urls_go_to_the_browser_and_unknown_schemes_are_dropped()
    {
        var request = StartupArguments.Parse(["https://example.com/a?b=1", "ftp://example.com/x", "mailto:a@b"]);

        Assert.Equal(["https://example.com/a?b=1"], request.Urls);
        Assert.Empty(request.Files);
    }

    [Fact]
    public void The_browser_option_turns_an_html_file_into_a_file_url()
    {
        var folder = NewFolder();
        var html = Path.Combine(folder.FullName, "page.html");
        File.WriteAllText(html, "<p>x</p>");

        var request = StartupArguments.Parse(["--browser", html]);

        Assert.Equal([new Uri(html).AbsoluteUri], request.Urls);
        Assert.Empty(request.Files);
    }

    [Fact]
    public void Missing_paths_and_no_arguments_make_an_empty_request()
    {
        Assert.True(StartupArguments.Parse([]).IsEmpty);
        Assert.True(StartupArguments.Parse([Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt")]).IsEmpty);
    }
}
