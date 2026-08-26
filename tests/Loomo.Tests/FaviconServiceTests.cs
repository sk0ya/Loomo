using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ブックマークの行に出すサイトのアイコン（§21.5.1・<see cref="FaviconService"/>）の検証。
/// 通信は行わない——ここで確かめたいのは「鍵の作り方」「置き場の当て方」「取りに行って良い場面の
/// 線引き」で、そのどれもが通信の手前にある判断だから。
/// </summary>
public class FaviconServiceTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loomo-favicons-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    // ===== 鍵（ホスト単位） =====

    [Fact]
    public void Site_key_is_the_host_so_pages_of_one_site_share_one_icon()
    {
        Assert.Equal("example.com", FaviconService.SiteKey("https://example.com/a/b?c=d#e"));
        Assert.Equal(FaviconService.SiteKey("https://example.com/a"),
            FaviconService.SiteKey("https://example.com/z/z/z"));
    }

    [Fact]
    public void Site_key_is_case_insensitive_and_keeps_a_non_default_port()
    {
        Assert.Equal("example.com", FaviconService.SiteKey("HTTPS://Example.COM/"));
        Assert.Equal("localhost:5173", FaviconService.SiteKey("http://localhost:5173/index.html"));
        // 既定ポートは綴りが違っても同じサイト。
        Assert.Equal(FaviconService.SiteKey("https://example.com/"),
            FaviconService.SiteKey("https://example.com:443/"));
    }

    [Theory]
    [InlineData("file:///C:/notes/a.html")]
    [InlineData("about:blank")]
    [InlineData("chrome-extension://abc/popup.html")]
    [InlineData("")]
    [InlineData(null)]
    public void Site_key_is_null_for_addresses_that_are_not_a_web_site(string? url)
        => Assert.Null(FaviconService.SiteKey(url));

    [Fact]
    public void Cache_file_names_do_not_collide_after_sanitizing()
    {
        // ':' を伏せると同じ綴りになる2つ。指紋が付くので別ファイルになる。
        Assert.NotEqual(FaviconService.CacheFileName("localhost:8080"),
            FaviconService.CacheFileName("localhost_8080"));
        Assert.DoesNotContain(':', FaviconService.CacheFileName("localhost:8080"));
    }

    // ===== HTML から <link rel="icon"> を拾う =====

    [Fact]
    public void Icon_links_are_collected_with_apple_touch_icons_last()
    {
        var html = """
            <head>
            <link rel="stylesheet" href="/style.css">
            <link rel="apple-touch-icon" href="/apple.png">
            <link rel="shortcut icon" href='/small.ico'>
            </head>
            """;
        Assert.Equal(new[] { "/small.ico", "/apple.png" }, FaviconService.ParseIconLinks(html).ToArray());
    }

    [Fact]
    public void Svg_icons_are_skipped_because_wpf_cannot_draw_them()
    {
        var html = """<link rel="icon" href="/icon.svg"><link rel="icon" href="/icon.png">""";
        Assert.Equal(new[] { "/icon.png" }, FaviconService.ParseIconLinks(html).ToArray());
    }

    // ===== 置き場（ディスク） =====

    [Fact]
    public async Task Icon_on_disk_is_used_without_touching_the_network()
    {
        var directory = TempDirectory();
        var service = new FaviconService(directory);
        File.WriteAllBytes(
            Path.Combine(directory, FaviconService.CacheFileName("example.com") + ".png"),
            OnePixelPng());

        // 通信を許していないのに絵が出る＝ディスクで当たっている。
        var icon = await service.GetAsync("https://example.com/deep/page", allowNetwork: false);
        Assert.NotNull(icon);

        // 2度目はメモリで返る（同期完了）。
        var again = service.GetAsync("https://example.com/other", allowNetwork: false);
        Assert.True(again.IsCompletedSuccessfully);
        Assert.Same(icon, await again);
        Assert.Same(icon, service.TryGetCached("https://example.com/"));
    }

    [Fact]
    public async Task Cache_only_lookups_do_not_record_a_miss()
    {
        var directory = TempDirectory();
        var service = new FaviconService(directory);

        Assert.Null(await service.GetAsync("https://example.com/", allowNetwork: false));

        // 「取れなかった」を覚えるのは取りに行った側だけ。手元を見ただけで覚えると、
        // あとからブックマークの行が来ても二度と取りに行かなくなる。
        Assert.Empty(Directory.GetFiles(directory));
        Assert.Null(service.TryGetCached("https://example.com/"));
    }

    [Fact]
    public async Task Harvested_icon_is_kept_in_memory_and_on_disk()
    {
        var directory = TempDirectory();
        var service = new FaviconService(directory);

        await service.HarvestAsync("https://example.com/page", OnePixelPng());

        var icon = await service.GetAsync("https://example.com/another", allowNetwork: false);
        Assert.NotNull(icon);
        Assert.Single(Directory.GetFiles(directory, "*.png"));
    }

    [Fact]
    public async Task Harvest_wins_over_a_host_already_remembered_as_hopeless()
    {
        var directory = TempDirectory();
        var service = new FaviconService(directory);

        // 取りに行って外した（bot 避けで断られるサイト等）＝「無い」と覚えた状態を作る。
        // 通信は届かない前提のホストを使うので、これは確実に外れる。
        Assert.Null(await service.GetAsync("https://loomo.invalid/", allowNetwork: true));
        Assert.Single(Directory.GetFiles(directory, "*.miss"));

        // そこへ人がページを開いて絵を持って来た。ここで弾いてしまうと、
        // 取りに行けないサイトは何度開いても ★ のままになる。
        await service.HarvestAsync("https://loomo.invalid/page", OnePixelPng());

        Assert.NotNull(service.TryGetCached("https://loomo.invalid/other"));
        Assert.Single(Directory.GetFiles(directory, "*.png"));
        Assert.Empty(Directory.GetFiles(directory, "*.miss"));   // 覚えた「無い」も消える
    }

    [Fact]
    public async Task A_cache_only_miss_does_not_stop_a_later_bookmark_row_from_fetching()
    {
        var directory = TempDirectory();
        var service = new FaviconService(directory);

        // 同じホストを、手元だけの引き（履歴・候補）と取りに行く引き（ブックマーク）で同時に頼む。
        // 束ねる鍵がホストだけだと、後から来たブックマークの行が手元だけの答え（null）に
        // 相乗りして、取りに行かないまま「絵が無い」になる。
        var cacheOnly = service.GetAsync("https://loomo.invalid/", allowNetwork: false);
        var bookmark = service.GetAsync("https://loomo.invalid/", allowNetwork: true);
        await Task.WhenAll(cacheOnly, bookmark);
        Assert.Null(await cacheOnly);
        Assert.Null(await bookmark);

        // 取りに行った側だけが「無い」を記録する。
        Assert.Single(Directory.GetFiles(directory, "*.miss"));
    }

    /// <summary>1px の PNG（中身は問わない——読めて凍る絵であれば良い）。</summary>
    private static byte[] OnePixelPng()
    {
        var source = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32,
            null, new byte[] { 0x20, 0x40, 0x80, 0xFF }, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }
}
