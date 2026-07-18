using sk0ya.Loomo.Core.Git;
using Xunit;

namespace sk0ya.Loomo.Tests;

public sealed class CommitStatLinksTests
{
    [Theory]
    [InlineData(" src/App.cs | 10 +++++-----", "src/App.cs")]
    [InlineData(" src/{Old.cs => New.cs} | 2 +-", "src/New.cs")]
    [InlineData(" old.txt => new.txt | Bin 1 -> 2 bytes", "new.txt")]
    public void TryParse_returns_navigation_target(string line, string expected)
    {
        var result = CommitStatLinks.TryParse(line);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.NavigatePath);
    }

    [Theory]
    [InlineData("commit abc123")]
    [InlineData(" 2 files changed, 3 insertions(+)")]
    public void TryParse_rejects_non_stat_lines(string line)
        => Assert.Null(CommitStatLinks.TryParse(line));
}
