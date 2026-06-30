using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class BrowserFileAccessTests
{
    [Theory]
    [InlineData("file:///C:/work/index.html")]
    [InlineData("file:///C:/work/assets/js/desk.js")]
    [InlineData("file:///C:/WORK/data.json")]
    public void Workspace配下のfileUrlを許可する(string uri)
    {
        Assert.True(BrowserFileAccess.IsAllowed(uri, @"C:\work"));
    }

    [Theory]
    [InlineData("file:///C:/workspace/index.html")]
    [InlineData("file:///C:/outside/secret.txt")]
    [InlineData("https://example.com/")]
    public void Workspace外とfile以外を許可しない(string uri)
    {
        Assert.False(BrowserFileAccess.IsAllowed(uri, @"C:\work"));
    }

    [Fact]
    public void Workspaceが無ければfileUrlを許可しない()
    {
        Assert.False(BrowserFileAccess.IsAllowed("file:///C:/work/index.html", null));
    }
}
