using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class FolderTreeKeyboardNavigationTests
{
    [Theory]
    [InlineData(3, 0, 1, 1)]
    [InlineData(3, 2, -1, 1)]
    [InlineData(3, 1, 1, 2)]
    [InlineData(3, 1, -1, 0)]
    public void FindAdjacentIndex_moves_in_display_order(int count, int current, int delta, int expected)
    {
        Assert.Equal(expected,
            FolderTreeKeyboardNavigation.FindAdjacentIndex(count, current, delta));
    }

    [Theory]
    [InlineData(3, 0, -1, 0)]
    [InlineData(3, 2, 1, 2)]
    public void FindAdjacentIndex_stops_at_display_edges(int count, int current, int delta, int expected)
    {
        Assert.Equal(expected,
            FolderTreeKeyboardNavigation.FindAdjacentIndex(count, current, delta));
    }

    [Fact]
    public void FindAdjacentIndex_starts_at_first_node_when_selection_is_missing()
    {
        Assert.Equal(0, FolderTreeKeyboardNavigation.FindAdjacentIndex(3, -1, 1));
        Assert.Equal(0, FolderTreeKeyboardNavigation.FindAdjacentIndex(3, -1, -1));
        Assert.Equal(0, FolderTreeKeyboardNavigation.FindAdjacentIndex(3, 99, 1));
    }

    [Fact]
    public void FindAdjacentIndex_returns_minus_one_for_empty_display()
    {
        Assert.Equal(-1, FolderTreeKeyboardNavigation.FindAdjacentIndex(0, -1, 1));
        Assert.Equal(-1, FolderTreeKeyboardNavigation.FindAdjacentIndex(-1, 0, -1));
    }

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
