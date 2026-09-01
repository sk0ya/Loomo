using System.IO;
using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.Tests;

public sealed class NavigationSourceContextTests
{
    [Fact]
    public void Reads_context_with_a_marker_on_the_target_line()
    {
        var path = Path.Combine(Path.GetTempPath(), "LoomoPeek_" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllLines(path, ["one", "two", "three", "four", "five"]);
        try
        {
            var context = NavigationSourceContext.Read(path, 2, radius: 1);

            Assert.Equal("     2  two\n▶    3  three\n     4  four", context);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Returns_empty_for_missing_or_invalid_locations()
    {
        Assert.Equal("", NavigationSourceContext.Read(
            Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")), 0));
        Assert.Equal("", NavigationSourceContext.Read("", 0));
        Assert.Equal("", NavigationSourceContext.Read("anything.cs", -1));
    }
}
