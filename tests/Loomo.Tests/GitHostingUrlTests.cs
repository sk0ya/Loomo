using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

public sealed class GitHostingUrlTests
{
    [Theory]
    [InlineData("https://github.com/sk0ya/Loomo.git", "https://github.com/sk0ya/Loomo")]
    [InlineData("git@github.com:sk0ya/Loomo.git", "https://github.com/sk0ya/Loomo")]
    [InlineData("ssh://git@github.com/sk0ya/Loomo", "https://github.com/sk0ya/Loomo")]
    [InlineData("https://github.com/sk0ya/Loomo/", "https://github.com/sk0ya/Loomo")]
    public void GitHubリモートをWebリポジトリURLへ変換する(string remote, string expected)
    {
        Assert.True(GitHostingUrl.TryGetGitHubRepositoryUrl(remote, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("https://gitlab.com/sk0ya/Loomo.git")]
    [InlineData("https://github.com/sk0ya/Loomo/issues")]
    [InlineData("git@example.com:sk0ya/Loomo.git")]
    [InlineData(null)]
    public void GitHub以外またはリポジトリ以外は対象外(string? remote)
    {
        Assert.False(GitHostingUrl.TryGetGitHubRepositoryUrl(remote, out _));
    }
}
