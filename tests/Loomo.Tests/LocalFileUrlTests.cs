using System.IO;
using System.Text;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>ローカルファイルをパスだけで開く口（https://file.loomo/C:/…）。マップもコピーも無しに
/// URL→パスが一意に戻ること、余計なところへ手が届かないことを確かめる。</summary>
public class LocalFileUrlTests
{
    [Theory]
    [InlineData(@"C:\proj\docs\index.html", "https://file.loomo/C:/proj/docs/index.html")]
    [InlineData(@"C:\my dir\a#b.html", "https://file.loomo/C:/my%20dir/a%23b.html")]
    [InlineData(@"D:\メモ\x.htm", "https://file.loomo/D:/%E3%83%A1%E3%83%A2/x.htm")]
    [InlineData(@"\\srv\share\site\x.html", "https://file.loomo/unc/srv/share/site/x.html")]
    public void パスとURLが往復する(string path, string url)
    {
        Assert.Equal(url, LocalFileUrl.FromPath(path));
        Assert.True(LocalFileUrl.TryToPath(url, out var back));
        Assert.Equal(path, back);
    }

    [Fact]
    public void 相対パスはふつうのURL解決でディスクの並びどおりに解ける()
    {
        var page = new Uri(LocalFileUrl.FromPath(@"C:\proj\docs\index.html"));
        Assert.True(LocalFileUrl.TryToPath(new Uri(page, "../assets/app.js").AbsoluteUri, out var path));
        Assert.Equal(@"C:\proj\assets\app.js", path);
    }

    [Theory]
    [InlineData("https://example.com/C:/a.html")]
    [InlineData("https://file.loomo/")]
    [InlineData("https://file.loomo/notadrive/a.html")]
    [InlineData("https://file.loomo/C:/a%5C..%5Cb.html")]   // エスケープした区切り
    [InlineData("https://file.loomo/C:/a.txt:stream")]      // 代替データストリーム
    [InlineData("https://file.loomo/unc/srv")]
    [InlineData(null)]
    public void 組めないURLはパスにしない(string? url)
        => Assert.False(LocalFileUrl.TryToPath(url, out _));

    [Theory]
    [InlineData("file:///C:/notes/a.html?x=1#top", "https://file.loomo/C:/notes/a.html?x=1#top")]
    [InlineData("file:///C:/notes/a.pdf", null)]   // PDF やメディアは file:// のままで困らない
    [InlineData("https://example.com/a.html", null)]
    public void fileのHTMLだけ付け替える(string uri, string? expected)
        => Assert.Equal(expected, LocalFileUrl.TryFromFileUri(uri));

    /// <summary>全ローカルファイルが1つのオリジンなので、オリジンでは線を引けない。範囲で引く：
    /// 開いた HTML がディスク中を fetch で読めてはいけない（file:// では Chromium が止めていた）。</summary>
    [Theory]
    [InlineData(@"C:\work\app", @"C:\work\app\data\x.json", true)]
    [InlineData(@"C:\work\app", @"C:\work\app", true)]
    [InlineData(@"C:\work\app\", @"C:\WORK\APP\x.js", true)]
    [InlineData(@"C:\work\app", @"C:\work\app2\x.js", false)]          // 区切りまで見る
    [InlineData(@"C:\work\app", @"C:\Users\me\.ssh\id_rsa", false)]
    [InlineData(null, @"C:\work\app\x.js", false)]                     // ネットのページが表：何も読ませない
    public void 部品の読み出しは表のページの範囲の中だけ(string? scope, string path, bool allowed)
        => Assert.Equal(allowed, LocalFileAccess.IsWithin(scope, path));

    [Fact]
    public void 範囲はページを担当するワークスペースフォルダーで外ならページのフォルダー()
    {
        static string? Workspace(string p)
            => p.StartsWith(@"C:\work\app\", StringComparison.OrdinalIgnoreCase) ? @"C:\work\app" : null;

        Assert.Equal(@"C:\work\app", LocalFileAccess.ScopeOf(@"C:\work\app\site\index.html", Workspace));
        Assert.Equal(@"C:\Users\me\Downloads", LocalFileAccess.ScopeOf(@"C:\Users\me\Downloads\x.html", Workspace));
        Assert.Equal(@"C:\Users\me\Downloads", LocalFileAccess.ScopeOf(@"C:\Users\me\Downloads\x.html", null));
    }

    [Theory]
    [InlineData(true, false, true)]     // ローカルページから別のローカルページへ
    [InlineData(false, true, true)]     // 人のクリック・Loomo 自身の遷移
    [InlineData(false, false, false)]   // ネットのページのスクリプトがローカルへ飛ばす
    public void ネットのページのスクリプトからローカルへは飛ばせない(bool fromLocal, bool userInitiated, bool allowed)
        => Assert.Equal(allowed, LocalFileAccess.AllowsTopLevelNavigation(fromLocal, userInitiated));

    [Theory]
    [InlineData("bytes=0-9", 100, 0, 9)]
    [InlineData("bytes=90-", 100, 90, 99)]
    [InlineData("bytes=-10", 100, 90, 99)]
    [InlineData("bytes=50-500", 100, 50, 99)]
    public void 単一範囲を読む(string header, long length, long start, long end)
    {
        Assert.True(LocalFileResponse.TryParseRange(header, length, out var s, out var e));
        Assert.Equal((start, end), (s, e));
    }

    [Theory]
    [InlineData("bytes=0-1,5-6")]
    [InlineData("bytes=200-")]
    [InlineData("items=0-1")]
    public void 受けない範囲は全体扱い(string header)
        => Assert.False(LocalFileResponse.TryParseRange(header, 100, out _, out _));

    [Fact]
    public void ディスクの中身をそのまま返しフォルダーはindexを返す()
    {
        var folder = Path.Combine(Path.GetTempPath(), "loomo-localfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "index.html"), "<p>hi</p>");
            File.WriteAllText(Path.Combine(folder, "app.mjs"), "export {}");

            var page = LocalFileResponse.Create(folder + Path.DirectorySeparatorChar, range: null);
            Assert.Equal(200, page.Status);
            Assert.Contains("Content-Type: text/html", page.Headers);
            Assert.Equal("<p>hi</p>", new StreamReader(page.Content, Encoding.UTF8).ReadToEnd());

            var module = LocalFileResponse.Create(Path.Combine(folder, "app.mjs"), "bytes=0-5");
            Assert.Equal(206, module.Status);
            Assert.Contains("Content-Type: text/javascript", module.Headers);
            Assert.Contains("Content-Range: bytes 0-5/9", module.Headers);
            Assert.Equal("export", new StreamReader(module.Content).ReadToEnd());

            Assert.Equal(404, LocalFileResponse.Create(Path.Combine(folder, "none.html"), null).Status);

            // 末尾 / 無しのフォルダーは / 付きへ移す（でないと相対リンクが1つ上を基準に解ける）。
            var url = LocalFileUrl.FromPath(folder);
            var moved = LocalFileResponse.Create(folder, null, url + "?q=1");
            Assert.Equal(301, moved.Status);
            Assert.Contains($"Location: {url}/?q=1", moved.Headers);
            Assert.Equal(200, LocalFileResponse.Create(folder, null, url + "/").Status);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
