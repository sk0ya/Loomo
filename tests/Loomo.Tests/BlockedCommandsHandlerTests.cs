using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class BlockedCommandsHandlerTests
{
    [Fact]
    public void ParsePatterns_ignores_comments_and_blank_lines()
    {
        var result = BlockedCommandsHandler.ParsePatterns("# comment\r\n\r\n rm .* \r\nformat c:");

        Assert.Equal(new[] { "rm .*", "format c:" }, result);
    }
}
