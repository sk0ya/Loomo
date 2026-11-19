using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>失敗したテストのメッセージから期待値と実際を切り出す（§24.110）。</summary>
public class TestAssertionMessageTests
{
    [Fact]
    public void XUnit_StringsDiffer_DropsPositionMarkers()
    {
        var message = "Assert.Equal() Failure: Strings differ\r\n" +
                      "           ↓ (pos 2)\r\n" +
                      "Expected: \"abcde\"\r\n" +
                      "Actual:   \"abXde\"\r\n" +
                      "           ↑ (pos 2)";

        var values = TestAssertionMessage.TryParse(message);

        Assert.NotNull(values);
        Assert.Equal("abcde", values!.Expected);
        Assert.Equal("abXde", values.Actual);
    }

    [Fact]
    public void XUnit_EscapedNewlines_BecomeLines()
    {
        var message = "Assert.Equal() Failure: Strings differ\nExpected: \"a\\nb\\nc\"\nActual:   \"a\\nB\\nc\"";

        var values = TestAssertionMessage.TryParse(message)!;

        Assert.Equal("a\nb\nc", values.Expected);
        Assert.Equal("a\nB\nc", values.Actual);
    }

    [Fact]
    public void XUnit_Numbers_AndStackTraceIgnored()
    {
        var message = "Assert.Equal() Failure: Values differ\nExpected: 3\nActual:   4\n   at Foo.Bar() in C:\\x.cs:line 3";

        var values = TestAssertionMessage.TryParse(message)!;

        Assert.Equal("3", values.Expected);
        Assert.Equal("4", values.Actual);
    }

    [Fact]
    public void NUnit_ButWas()
    {
        var message = "  Expected string length 3 but was 3. Strings differ at index 2.\n" +
                      "  Expected: \"abc\"\n" +
                      "  But was:  \"abd\"\n" +
                      "  -------------^";

        var values = TestAssertionMessage.TryParse(message)!;

        Assert.Equal("abc", values.Expected);
        Assert.Equal("abd", values.Actual);
    }

    [Fact]
    public void MSTest_AngleBrackets_KeepMultilineValues()
    {
        var message = "Assert.AreEqual failed. Expected:<line1\nline2>. Actual:<line1\nLINE2>. ";

        var values = TestAssertionMessage.TryParse(message)!;

        Assert.Equal("line1\nline2", values.Expected);
        Assert.Equal("line1\nLINE2", values.Actual);
    }

    [Fact]
    public void Jest_Received_WithAnsiColors()
    {
        var message = "expect(received).toBe(expected)\n\nExpected: \u001b[32m\"left\"\u001b[39m\nReceived: \u001b[31m\"right\"\u001b[39m";

        var values = TestAssertionMessage.TryParse(message)!;

        Assert.Equal("left", values.Expected);
        Assert.Equal("right", values.Actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("System.NullReferenceException: Object reference not set")]
    [InlineData("Expected: 3")]
    [InlineData("Expected: 3\nActual:   3")]
    public void Unreadable_ReturnsNull(string? message)
        => Assert.Null(TestAssertionMessage.TryParse(message));
}
