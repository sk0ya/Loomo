using System.IO;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class StartupArgumentsTests
{
    [Fact]
    public void Accepts_a_folder_as_a_positional_argument()
    {
        var folder = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-startup-{Guid.NewGuid():N}"));

        var actual = StartupArguments.TryGetWorkspaceFolder([folder.FullName]);

        Assert.Equal(Path.GetFullPath(folder.FullName), actual);
    }

    [Fact]
    public void Accepts_the_explicit_workspace_option_and_ignores_files()
    {
        var folder = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"loomo-startup-{Guid.NewGuid():N}"));
        var file = Path.Combine(folder.FullName, "file.txt");
        File.WriteAllText(file, "test");

        var actual = StartupArguments.TryGetWorkspaceFolder(["--workspace", file, folder.FullName]);

        Assert.Equal(Path.GetFullPath(folder.FullName), actual);
    }
}
