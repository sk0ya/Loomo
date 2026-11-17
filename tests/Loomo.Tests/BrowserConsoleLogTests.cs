using System.Linq;
using sk0ya.Loomo.Services.Browser;

namespace sk0ya.Loomo.Tests;

/// <summary>ブラウザのページのエラーを読む（§45.5 P2 `browser_errors`）。例外と error・warning だけを、ローカルのページについてだけ読む。</summary>
public sealed class BrowserConsoleLogTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Fact]
    public void 未処理の例外はURLと行と説明を読む()
    {
        var json = """{"timestamp":1,"exceptionDetails":{"exceptionId":1,"text":"Uncaught","lineNumber":41,"columnNumber":3,"url":"http://localhost:3000/app.js","exception":{"type":"object","description":"TypeError: x is undefined"}}}""";
        var entry = BrowserConsoleLog.ParseException(json, At)!;
        Assert.Equal("例外", entry.Kind);
        Assert.Equal("TypeError: x is undefined", entry.Text);
        Assert.Equal("http://localhost:3000/app.js", entry.Url);
        Assert.Equal(42, entry.Line);   // DevTools は 0 始まり
    }

    [Fact]
    public void console_error_と_warning_は読み_log_は読まない()
    {
        var error = """{"type":"error","args":[{"type":"string","value":"failed to load"},{"type":"number","value":7}],"stackTrace":{"callFrames":[{"url":"http://localhost/a.js","lineNumber":4}]}}""";
        var entry = BrowserConsoleLog.ParseConsole(error, At)!;
        Assert.Equal("エラー", entry.Kind);
        Assert.Equal("failed to load 7", entry.Text);
        Assert.Equal(5, entry.Line);

        var warning = """{"type":"warning","args":[{"type":"string","value":"deprecated"}]}""";
        Assert.Equal("警告", BrowserConsoleLog.ParseConsole(warning, At)!.Kind);

        Assert.Null(BrowserConsoleLog.ParseConsole("""{"type":"log","args":[{"type":"string","value":"hi"}]}""", At));
    }

    [Fact]
    public void 溜めすぎず_新しいものから返す()
    {
        var sut = new BrowserConsoleLog();
        for (var i = 0; i < BrowserConsoleLog.Capacity + 5; i++)
            sut.Add(new BrowserConsoleEntry("エラー", "http://localhost/", 0, "e" + i, At));
        var recent = sut.Recent(3).Select(e => e.Text).ToList();
        Assert.Equal(["e" + (BrowserConsoleLog.Capacity + 4), "e" + (BrowserConsoleLog.Capacity + 3), "e" + (BrowserConsoleLog.Capacity + 2)], recent);
    }

    [Fact]
    public void 長い文は切り_読めない_JSON_は無視する()
    {
        var long_ = new string('x', BrowserConsoleLog.MaxText + 50);
        var entry = BrowserConsoleLog.ParseConsole("{\"type\":\"error\",\"args\":[{\"type\":\"string\",\"value\":\"" + long_ + "\"}]}", At)!;
        Assert.True(entry.Text.Length <= BrowserConsoleLog.MaxText + 1);
        Assert.Null(BrowserConsoleLog.ParseException("{not json", At));
    }

    [Theory]
    [InlineData("http://localhost:3000/", true)]
    [InlineData("http://127.0.0.1:8080/x", true)]
    [InlineData("https://file.loomo/C:/Projects/app/index.html", true)]
    [InlineData("file:///C:/Projects/app/index.html", true)]
    [InlineData("file:///C:/Users/koya/Documents/secret.html", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://dev.azure.com/org/project", false)]
    [InlineData("", false)]
    public void 読んでよいのはローカルのページだけ(string url, bool expected)
        => Assert.Equal(expected, BrowserConsoleLog.IsLocalPage(url, ["C:\\Projects\\app"]));
}
