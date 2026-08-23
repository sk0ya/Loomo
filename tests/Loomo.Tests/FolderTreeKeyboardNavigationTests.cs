using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class FolderTreeKeyboardNavigationTests
{
    [Fact]
    public void TypeAhead_starts_after_current_item_and_wraps()
    {
        var names = new[] { "app.cs", "assets", "app.test.cs", "README.md" };

        Assert.Equal(1, FolderTreeKeyboardNavigation.FindTypeAheadMatch(names, "a", 0));
        Assert.Equal(0, FolderTreeKeyboardNavigation.FindTypeAheadMatch(names, "a", 2));
    }

    [Fact]
    public void TypeAhead_continuation_keeps_current_match_as_candidate()
    {
        var names = new[] { "src", "tests", "tools" };

        Assert.Equal(1, FolderTreeKeyboardNavigation.FindTypeAheadMatch(names, "te", 1));
    }

    [Fact]
    public void TypeAhead_returns_no_match_without_moving_selection()
    {
        var names = new[] { "src", "tests", "tools" };

        Assert.Equal(-1, FolderTreeKeyboardNavigation.FindTypeAheadMatch(names, "zz", 0));
    }
}
