using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// ログ（.log）の色分けビューア（LogEditorSupport / LogLineClassifier / LogRenderer）の検証。
/// </summary>
public class LogEditorSupportTests
{
    private static EditorSupportRegistry CreateRegistry()
        => new(new IEditorSupportProvider[]
        {
            new LogEditorSupport(new AiSettings()),
        });

    [Theory]
    [InlineData(@"C:\work\app.log")]
    [InlineData(@"C:\work\UPPER.LOG")]
    public void Resolve_LogファイルにはLogプロバイダを返す(string path)
    {
        var provider = CreateRegistry().Resolve(path);

        Assert.IsType<LogEditorSupport>(provider);
    }

    [Fact]
    public void DescribeTitle_Logプレフィックスとファイル名()
    {
        var support = new LogEditorSupport(new AiSettings());

        Assert.Equal("Log: app.log", support.DescribeTitle(@"C:\work\app.log"));
        Assert.IsAssignableFrom<IEditorSupportIncrementalHtmlProvider>(support);
    }

    [Theory]
    [InlineData("[ERROR] boom", LogLevel.Error)]
    [InlineData("2020-01-01 12:00:00 WARN foo happened", LogLevel.Warn)]
    [InlineData("INFO starting up", LogLevel.Info)]
    [InlineData("debug: cache miss", LogLevel.Debug)]
    [InlineData("2020-01-01 TRACE entering method", LogLevel.Trace)]
    [InlineData("FATAL unrecoverable crash", LogLevel.Fatal)]
    [InlineData("CRITICAL disk full", LogLevel.Fatal)]
    [InlineData("level=error something broke", LogLevel.Error)]
    [InlineData("this is a plain message", LogLevel.None)]
    [InlineData("    at Foo.Bar() in file.cs:line 3", LogLevel.None)]
    [InlineData("", LogLevel.None)]
    public void Classify_代表的な行を期待レベルに分類する(string line, LogLevel expected)
    {
        Assert.Equal(expected, LogLineClassifier.Classify(line));
    }

    [Fact]
    public void Classify_レベルらしい別語は誤検出しない()
    {
        // "errorCode" のような単語の一部はレベルとして拾わない（区切りで囲まれていない）。
        Assert.Equal(LogLevel.None, LogLineClassifier.Classify("errorCode=0 terminated normally"));
    }

    [Fact]
    public void RenderHtml_ページシェルとツールバーとタイトルを含む()
    {
        var support = new LogEditorSupport(new AiSettings());

        var html = support.RenderHtml(@"C:\work\app.log", "INFO hello");

        Assert.Contains("Log: app.log", html);        // <title>
        Assert.Contains("id=\"log-root\"", html);
        Assert.Contains("id=\"log-filter\"", html);    // 絞り込みツールバー
        Assert.Contains("lvl-toggle", html);           // レベル表示トグル
        Assert.Contains("data-level=\"error\"", html);
    }

    [Fact]
    public void RenderBody_各行にレベルクラスを付ける()
    {
        var support = new LogEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\app.log", "[ERROR] boom\nWARN careful\nplain line");

        Assert.Contains("class=\"logline lv-error\"", body);
        Assert.Contains("data-level=\"error\"", body);
        Assert.Contains("class=\"logline lv-warn\"", body);
        Assert.Contains("class=\"logline lv-none\"", body);
        Assert.Contains("<span class=\"ln\">1</span>", body);   // 行番号
    }

    [Fact]
    public void RenderBody_HTML特殊文字をエスケープする()
    {
        var support = new LogEditorSupport(new AiSettings());

        var body = support.RenderBody(@"C:\work\app.log", "ERROR <script>alert(1)</script>");

        Assert.Contains("&lt;script&gt;", body);
        Assert.DoesNotContain("<script>alert(1)</script>", body);
    }

    [Fact]
    public void RenderBody_空テキストは空表示にする()
    {
        var support = new LogEditorSupport(new AiSettings());

        Assert.Contains("空のファイル", support.RenderBody(@"C:\work\app.log", ""));
        Assert.Contains("空のファイル", support.RenderBody(@"C:\work\app.log", "\n"));
    }
}
