using System.Text.Json;
using sk0ya.Loomo.App.Views;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 拡張機能のページから届く「このページを開いてくれ」の受け口（§21.5.2）。
/// ページは何でも送れるので、<b>誰の合図か</b>と<b>どこへ行くか</b>を両方見て弾けることを確かめる。
/// </summary>
public class ExtensionPageBridgeTests
{
    private static string Message(string url) => $"{{\"loomo\":\"openExtensionPage\",\"url\":\"{url}\"}}";

    private const string PopupPage = "chrome-extension://cjpalhdlnbpafiamejdnhcphjbkeiagm/popup.html";

    [Fact]
    public void 拡張機能のページからの設定画面要求を受ける()
    {
        var options = "chrome-extension://cjpalhdlnbpafiamejdnhcphjbkeiagm/dashboard.html";

        Assert.True(ExtensionPageBridge.TryReadOpenRequest(Message(options), PopupPage, out var url));
        Assert.Equal(options, url);
    }

    /// <summary>postMessage(string) は JSON 文字列として届く（二重に解く必要がある）。</summary>
    [Fact]
    public void 文字列として届いた合図も解く()
    {
        var inner = Message("https://example.com/help");

        Assert.True(ExtensionPageBridge.TryReadOpenRequest(JsonSerializer.Serialize(inner), PopupPage, out var url));
        Assert.Equal("https://example.com/help", url);
    }

    /// <summary>普通のページから拡張機能のページを開かせる口にしない。</summary>
    [Fact]
    public void 拡張機能以外のページからは受けない()
        => Assert.False(ExtensionPageBridge.TryReadOpenRequest(
            Message("chrome-extension://cjpalhdlnbpafiamejdnhcphjbkeiagm/dashboard.html"),
            "https://example.com/", out _));

    [Theory]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ページですらない")]
    public void 開いてよいスキーム以外は弾く(string target)
        => Assert.False(ExtensionPageBridge.TryReadOpenRequest(Message(target), PopupPage, out _));

    /// <summary>ページが送る他の web message（型違い・別の用件）は素通しする。</summary>
    [Theory]
    [InlineData("{\"loomo\":1}")]
    [InlineData("{\"loomo\":\"installExtension\"}")]
    [InlineData("{\"loomo\":\"openExtensionPage\"}")]
    [InlineData("{\"loomo\":\"openExtensionPage\",\"url\":42}")]
    [InlineData("こわれたJSON")]
    public void 他のメッセージは素通しする(string json)
        => Assert.False(ExtensionPageBridge.TryReadOpenRequest(json, PopupPage, out _));
}
