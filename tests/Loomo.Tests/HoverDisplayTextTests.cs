using sk0ya.Loomo.App.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 「この位置の説明を表示」に出す本文の整形（<see cref="HoverDisplayText"/>）。
/// 言語サーバーは hover を Markdown で返すので、素通しだと <c>```csharp</c> という囲いや
/// <c>\_value</c> というエスケープがそのまま画面に出る（実測でそうなっていた）。
/// </summary>
public sealed class HoverDisplayTextTests
{
    [Fact]
    public void コードフェンスを落として中身だけ残す()
    {
        var text = HoverDisplayText.Plain("```csharp\nstring Feature._value\n```\n\n概要です。");

        Assert.Equal("string Feature._value" + System.Environment.NewLine +
                     System.Environment.NewLine + "概要です。", text);
    }

    /// <summary>フェンスを落とした結果として空行が続くので、段落の区切りは 1 行に畳む。</summary>
    [Fact]
    public void 空行の連続は1行に畳む()
    {
        var text = HoverDisplayText.Plain("A\n\n\n\nB");

        Assert.Equal("A" + System.Environment.NewLine + System.Environment.NewLine + "B", text);
    }

    [Fact]
    public void Markdownのエスケープを外す()
    {
        var text = HoverDisplayText.Plain(@"ここでは、'\_value' は null ではありません。");

        Assert.Equal("ここでは、'_value' は null ではありません。", text);
    }

    /// <summary>記号の前でないバックスラッシュは本文（パスや文字列リテラル）なので残す。</summary>
    [Fact]
    public void 記号の前でないバックスラッシュは残す()
        => Assert.Equal(@"C:\work\a", HoverDisplayText.Plain(@"C:\work\a"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("```csharp\n```")]
    public void 中身が無ければnull(string? markdown)
        => Assert.Null(HoverDisplayText.Plain(markdown));
}
