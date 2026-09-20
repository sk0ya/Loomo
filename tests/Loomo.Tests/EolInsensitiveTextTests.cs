using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 「ディスクの内容と開いているバッファは同じか」の判定。以前は両方を正規化した<b>文字列を作って</b>比べていた
/// （全文の複製が3本・すべて UI スレッド）。作らない比較へ移したので、結果が前と同じであることを固定する。§31.15
/// </summary>
public class EolInsensitiveTextTests
{
    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\r\nb\rc\nd", "a\nb\nc\nd")]
    [InlineData("", "")]
    [InlineData("\r\n", "\n")]
    [InlineData("末尾の改行\r\n", "末尾の改行\n")]
    public void Only_the_spelling_of_the_line_break_differs(string left, string right)
        => Assert.True(EolInsensitiveText.Equals(left, right));

    [Theory]
    [InlineData("a\nb", "a\nc")]
    [InlineData("a\nb", "a\nb\n")]
    [InlineData("a\nb\n", "a\nb")]
    [InlineData("a b", "a\nb")]
    [InlineData("abc", "ab")]
    [InlineData("ab", "abc")]
    public void Anything_else_is_a_difference(string left, string right)
        => Assert.False(EolInsensitiveText.Equals(left, right));

    [Fact]
    public void Null_matches_only_null()
    {
        Assert.True(EolInsensitiveText.Equals(null, null));
        Assert.False(EolInsensitiveText.Equals(null, ""));
        Assert.False(EolInsensitiveText.Equals("", null));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc")]
    [InlineData("a\nb\nc")]
    [InlineData("\r\n\r\n")]
    [InlineData("改行なし")]
    public void It_agrees_with_the_normalizing_comparison_it_replaced(string text)
    {
        foreach (var other in new[] { text, text + "x", text.Replace("\r\n", "\n"), "" })
            Assert.Equal(Normalize(text) == Normalize(other), EolInsensitiveText.Equals(text, other));
    }

    /// <summary>書き換える前の式（ShellWindow.NormalizeEol）。</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");
}
