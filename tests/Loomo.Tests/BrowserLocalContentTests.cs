using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

public sealed class BrowserLocalContentTests
{
    [Fact]
    public void Workspace内のfileUrlを仮想HttpsUrlへ変換する()
    {
        var url = new Uri(@"C:\work space\app\index.html").AbsoluteUri;

        var actual = BrowserLocalContent.MapFileUrl(url, @"C:\work space");

        Assert.Equal("https://workspace.loomo/app/index.html", actual);
    }

    [Fact]
    public void 日本語を含むパスをUrlエンコードする()
    {
        var url = new Uri(@"C:\work\画面\一覧.html").AbsoluteUri;

        var actual = BrowserLocalContent.MapFileUrl(url, @"C:\work");

        Assert.Equal(
            "https://workspace.loomo/%E7%94%BB%E9%9D%A2/%E4%B8%80%E8%A6%A7.html",
            actual);
    }

    [Theory]
    [InlineData("https://example.com/index.html")]
    [InlineData("file:///C:/outside/index.html")]
    public void 通常UrlとWorkspace外のfileUrlは変更しない(string url)
    {
        Assert.Equal(url, BrowserLocalContent.MapFileUrl(url, @"C:\work"));
    }
}
