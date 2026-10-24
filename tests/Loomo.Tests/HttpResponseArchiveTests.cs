using System.IO;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Http;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>.http の応答を再起動をまたいで残す（§24.30.2）。</summary>
public sealed class HttpResponseArchiveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "loomo-http-archive-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static HttpExchangeResult Result(string body, DateTimeOffset at, params KeyValuePair<string, string>[] responseHeaders) => new(
        new PreparedHttpRequest("POST", "https://api.test/login",
            [KeyValuePair.Create("Authorization", "Bearer secret"), KeyValuePair.Create("Accept", "application/json")],
            "{\"user\":\"koya\"}", [], null),
        at, TimeSpan.FromMilliseconds(123), 200, "OK", "HTTP/1.1",
        responseHeaders, "application/json", body.Length, body, body, false, false, null);

    [Fact]
    public void Round_trip_keeps_what_the_panel_and_named_references_read()
    {
        var at = new DateTimeOffset(2026, 10, 24, 14, 3, 21, TimeSpan.FromHours(9));
        var json = HttpResponseArchive.Serialize("c:/w/api.http",
            [KeyValuePair.Create("@login", Result("{\"token\":\"日本語\"}", at, KeyValuePair.Create("X-Id", "7")))]);

        var (key, back) = Assert.Single(HttpResponseArchive.Deserialize(json));
        Assert.Equal("@login", key);
        Assert.Equal(200, back.StatusCode);
        Assert.Equal("{\"token\":\"日本語\"}", back.BodyText);
        Assert.Equal(at, back.StartedAt);
        Assert.Equal(123, back.Elapsed.TotalMilliseconds);
        Assert.Equal("7", back.ToNamedResponse().Headers.Single(h => h.Key == "X-Id").Value);
        Assert.Contains("日本語", json);   // \u にしない
    }

    [Fact]
    public void Secrets_are_masked_in_the_saved_copy()
    {
        var json = HttpResponseArchive.Serialize("f",
            [KeyValuePair.Create("k", Result("{}", DateTimeOffset.Now, KeyValuePair.Create("Set-Cookie", "sid=abc")))]);

        Assert.DoesNotContain("Bearer secret", json);
        Assert.DoesNotContain("sid=abc", json);
        var back = HttpResponseArchive.Deserialize(json).Single().Value;
        Assert.Equal(HttpResponseArchive.Masked, back.Request.Headers.Single(h => h.Key == "Authorization").Value);
        Assert.Equal("application/json", back.Request.Headers.Single(h => h.Key == "Accept").Value);
    }

    [Fact]
    public void Long_bodies_are_cut_and_marked_truncated()
    {
        var json = HttpResponseArchive.Serialize("f",
            [KeyValuePair.Create("k", Result(new string('a', HttpResponseArchive.MaxBodyChars + 10), DateTimeOffset.Now))]);
        var back = HttpResponseArchive.Deserialize(json).Single().Value;
        Assert.Equal(HttpResponseArchive.MaxBodyChars, back.BodyText.Length);
        Assert.True(back.IsTruncated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ broken")]
    [InlineData("{\"Version\":99,\"File\":\"f\",\"Entries\":[]}")]
    public void Unreadable_archives_are_empty(string json)
        => Assert.Empty(HttpResponseArchive.Deserialize(json));

    [Fact]
    public async Task A_sent_result_comes_back_in_a_new_session()
    {
        var file = Path.Combine(_folder, "api.http");
        const string text = "# @name login\nGET {{host}}/login\n";
        var document = HttpRequestFile.Parse(text);
        var block = document.Requests[0];

        // URL が組めない（{{host}} 未定義）ので通信せずに「送れなかった」結果が残る。
        using (var first = new HttpEditorSupport(_folder))
        {
            var sent = await first.SendAsync(file, document, block);
            Assert.NotNull(sent!.Error);
            first.FlushArchive();
        }

        using var second = new HttpEditorSupport(_folder);
        Assert.Null(second.LastResult(file, "@login"));
        await second.EnsureLoadedAsync(file);
        var restored = second.LastResult(file, "@login");
        Assert.NotNull(restored);
        Assert.Equal("GET", restored!.Request.Method);
    }

    [Fact]
    public async Task Restoring_does_not_overwrite_what_this_session_already_sent()
    {
        var file = Path.Combine(_folder, "api.http");
        Directory.CreateDirectory(_folder);
        using var support = new HttpEditorSupport(_folder);
        var stale = Result("old", DateTimeOffset.Now.AddDays(-1));
        File.WriteAllText(support.ArchivePath(HttpEditorSupport.Normalize(file)),
            HttpResponseArchive.Serialize(file, [KeyValuePair.Create("@login", stale)]));

        var document = HttpRequestFile.Parse("# @name login\nGET {{host}}/login\n");
        var sent = await support.SendAsync(file, document, document.Requests[0]);   // 送る前に読み戻し→送った結果で上書き

        Assert.Same(sent, support.LastResult(file, "@login"));
    }
}
