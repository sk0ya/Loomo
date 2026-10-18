using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ブラウザのタブから写す URL とリンク（§24.46）。</summary>
public sealed class BrowserTabLinkTests
{
    [Fact]
    public void ネットのページはそのまま()
        => Assert.Equal("https://example.com/a?b=1", BrowserTabLink.Url("https://example.com/a?b=1"));

    [Fact]
    public void ローカルのページは_file_URL_に戻す()
    {
        var url = LocalFileUrl.FromPath(@"C:\work\site\index.html") + "#top";

        Assert.Equal("file:///C:/work/site/index.html#top", BrowserTabLink.Url(url));
    }

    [Fact]
    public void 写せないものは無し()
    {
        Assert.Null(BrowserTabLink.Url(null));
        Assert.Null(BrowserTabLink.Url("about:blank"));
        Assert.Null(BrowserTabLink.Url("edge://settings"));
    }

    [Fact]
    public void Markdown_のリンクは題の括弧を逃がす()
        => Assert.Equal(@"[\[告知\] 新版](https://example.com/)",
            BrowserTabLink.Markdown("[告知] 新版", "https://example.com/"));

    [Fact]
    public void 題が空なら_URL_を題にする()
        => Assert.Equal("[https://example.com/](https://example.com/)", BrowserTabLink.Markdown("  ", "https://example.com/"));

    [Fact]
    public void 閉じ括弧を含む_URL_は山括弧で包む()
        => Assert.Equal("[Wiki](<https://ja.wikipedia.org/wiki/A_(B>)",
            BrowserTabLink.Markdown("Wiki", "https://ja.wikipedia.org/wiki/A_(B"));
}
