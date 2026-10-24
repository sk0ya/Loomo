using sk0ya.Loomo.Core.Http;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>.http の応答の中の URL から次の一手へ（§24.30.1）。</summary>
public sealed class HttpResponseLinksTests
{
    [Fact]
    public void Url_inside_a_json_string_is_found_without_quotes()
    {
        const string body = "{\n  \"next\": \"https://api.example.com/items?page=2\",\n  \"n\": 1\n}";
        var index = body.IndexOf("example", StringComparison.Ordinal);
        Assert.Equal("https://api.example.com/items?page=2", HttpResponseLinks.UrlAt(body, index));
    }

    [Fact]
    public void Trailing_punctuation_is_not_part_of_the_url()
    {
        const string body = "see https://example.com/docs.";
        Assert.Equal("https://example.com/docs", HttpResponseLinks.UrlAt(body, body.IndexOf("docs", StringComparison.Ordinal)));
        Assert.Null(HttpResponseLinks.UrlAt(body, body.Length - 1));   // 末尾の「.」の上は URL ではない
    }

    [Fact]
    public void Outside_a_url_is_null()
    {
        const string body = "{ \"id\": 1, \"url\": \"https://x.test/1\" }";
        Assert.Null(HttpResponseLinks.UrlAt(body, body.IndexOf("id", StringComparison.Ordinal)));
        Assert.Null(HttpResponseLinks.UrlAt("", 0));
    }

    [Fact]
    public void Appending_after_a_block_starts_a_new_section_before_the_next_one()
    {
        const string text = "### 一覧\nGET https://a.test/items\n\n### 次\nGET https://a.test/b\n";
        var doc = HttpRequestFile.Parse(text);
        var ins = HttpResponseLinks.AppendGet(text, doc.Requests[0], "https://a.test/items?page=2");

        var after = Apply(text, ins);
        var reparsed = HttpRequestFile.Parse(after);
        Assert.Equal(3, reparsed.Requests.Count);
        Assert.Equal("https://a.test/items?page=2", reparsed.Requests[1].Url);
        Assert.Equal("GET https://a.test/items?page=2", after.Split('\n')[ins.RequestLine]);
        Assert.Equal("https://a.test/b", reparsed.Requests[2].Url);   // 次の区画はそのまま
    }

    [Theory]
    [InlineData("GET https://a.test/x")]          // 改行で終わらない
    [InlineData("GET https://a.test/x\n")]        // 改行で終わる
    public void Appending_after_the_last_block_lands_at_the_end(string text)
    {
        var doc = HttpRequestFile.Parse(text);
        var ins = HttpResponseLinks.AppendGet(text, doc.Requests[^1], "https://a.test/y");

        var after = Apply(text, ins);
        var reparsed = HttpRequestFile.Parse(after);
        Assert.Equal(["https://a.test/x", "https://a.test/y"], reparsed.Requests.Select(r => r.Url));
        Assert.Equal("GET https://a.test/y", after.Split('\n')[ins.RequestLine]);
    }

    private static string Apply(string text, HttpRequestInsertion ins)
    {
        var lines = text.Split('\n');
        var offset = lines.Take(ins.Line).Sum(l => l.Length + 1) + ins.Column;
        return text.Insert(offset, ins.Text);
    }
}
