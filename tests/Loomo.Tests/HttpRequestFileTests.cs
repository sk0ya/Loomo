using System.Net;
using System.Net.Http;
using System.Text;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Http;

namespace sk0ya.Loomo.Tests;

/// <summary>.http（§24.30）。REST Client／JetBrains HTTP Client と同じ書式をそのまま読めること、
/// 変数が同じ意味で解決されること、送った結果が素材として読める形になることを見る。</summary>
public sealed class HttpRequestFileTests
{
    private const string Sample = """
        @host = https://api.example.com
        @token = abc

        ### ログイン
        # @name login
        POST {{host}}/login HTTP/1.1
        Content-Type: application/json

        {"user": "koya"}

        ### 一覧
        GET {{host}}/items
            ?page=1
            &size=20
        Authorization: Bearer {{login.response.body.$.token}}

        ###

        // メソッドを省略すると GET
        https://example.com/health
        """;

    [Fact]
    public void 区切りごとにリクエストを読む()
    {
        var doc = HttpRequestFile.Parse(Sample);

        Assert.Equal(3, doc.Requests.Count);
        var login = doc.Requests[0];
        Assert.Equal("POST", login.Method);
        Assert.Equal("{{host}}/login", login.Url);
        Assert.Equal("login", login.Name);
        Assert.Equal("ログイン", login.Title);
        Assert.Equal([new("Content-Type", "application/json")], login.Headers);
        Assert.Equal("""{"user": "koya"}""", login.Body);

        var list = doc.Requests[1];
        Assert.Equal("{{host}}/items?page=1&size=20", list.Url);
        Assert.Null(list.Body);
        Assert.Equal("一覧", list.DisplayName);

        var health = doc.Requests[2];
        Assert.Equal("GET", health.Method);
        Assert.Equal("https://example.com/health", health.Url);
        Assert.Equal("#3", health.DisplayName);
    }

    [Fact]
    public void ファイル変数を集める()
    {
        var doc = HttpRequestFile.Parse(Sample);

        Assert.Equal("https://api.example.com", doc.Variables["host"]);
        Assert.Equal("abc", doc.Variables["token"]);
    }

    [Fact]
    public void キャレットの行からリクエストを決める()
    {
        var doc = HttpRequestFile.Parse(Sample);
        var lines = Sample.Replace("\r\n", "\n").Split('\n');
        var itemsLine = Array.FindIndex(lines, l => l.StartsWith("GET "));

        Assert.Same(doc.Requests[1], doc.RequestAt(itemsLine + 2));
        Assert.Same(doc.Requests[0], doc.RequestAt(0));   // 先頭の変数の行は最初のリクエスト
        Assert.Same(doc.Requests[2], doc.RequestAt(lines.Length - 1));
    }

    [Fact]
    public void 応答ハンドラと書き出し指定は本文に混ぜない()
    {
        var doc = HttpRequestFile.Parse("""
            POST https://example.com/a
            Content-Type: text/plain

            hello
            > {%
                client.global.set("x", response.body.x);
            %}
            >> ./out.json
            """);

        Assert.Equal("hello", doc.Requests[0].Body);
    }

    [Fact]
    public void 本文をファイルから読む指定()
    {
        var doc = HttpRequestFile.Parse("""
            POST https://example.com/a

            < ./payload.json
            """);

        Assert.Null(doc.Requests[0].Body);
        Assert.Equal("./payload.json", doc.Requests[0].BodyFile);
    }

    [Fact]
    public void 次の区切りの上の説明コメントは本文に入れない()
    {
        var doc = HttpRequestFile.Parse("""
            POST https://example.com/a

            {"a": 1}

            # 次は削除
            ###
            DELETE https://example.com/a/1
            """);

        Assert.Equal("""{"a": 1}""", doc.Requests[0].Body);
        Assert.Equal("DELETE", doc.Requests[1].Method);
    }

    [Fact]
    public void 変数だけの区画はリクエストにしない()
        => Assert.Empty(HttpRequestFile.Parse("@a = 1\n# memo\n").Requests);

    [Fact]
    public void ファイル変数の中の変数も解決する()
    {
        var resolver = new HttpVariableResolver(new Dictionary<string, string>
        {
            ["host"] = "https://{{domain}}",
            ["domain"] = "example.com",
        });

        Assert.Equal("https://example.com/a", resolver.Resolve("{{host}}/a"));
        Assert.Empty(resolver.Unresolved);
    }

    [Fact]
    public void 解決できない変数は残して名前を集める()
    {
        var resolver = new HttpVariableResolver(new Dictionary<string, string>(), environment: _ => null);

        Assert.Equal("{{nope}}/a", resolver.Resolve("{{nope}}/a"));
        Assert.Equal(["nope"], resolver.Unresolved);
    }

    [Fact]
    public void 自分を参照する変数で止まらない()
    {
        var resolver = new HttpVariableResolver(new Dictionary<string, string> { ["a"] = "{{a}}x" });

        var value = resolver.Resolve("{{a}}");

        Assert.EndsWith("x", value);
    }

    [Fact]
    public void システム変数()
    {
        var now = new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.FromHours(9));
        var resolver = new HttpVariableResolver(
            new Dictionary<string, string>(),
            environment: name => name == "API_KEY" ? "k-1" : null,
            dotenv: name => name == "SECRET" ? "s-1" : null,
            now: () => now);

        Assert.Equal(now.ToUnixTimeSeconds().ToString(), resolver.Resolve("{{$timestamp}}"));
        Assert.True(Guid.TryParse(resolver.Resolve("{{$guid}}"), out _));
        Assert.Equal("k-1", resolver.Resolve("{{$processEnv API_KEY}}"));
        Assert.Equal("s-1", resolver.Resolve("{{$dotenv SECRET}}"));
        Assert.Equal("2026-10-18T00:30:00.0000000+00:00", resolver.Resolve("{{$datetime iso8601}}"));
        var random = int.Parse(resolver.Resolve("{{$randomInt 5 6}}"));
        Assert.Equal(5, random);
    }

    [Fact]
    public void 名前付きリクエストの応答から引く()
    {
        var login = new HttpNamedResponse(
            [new("Location", "/users/7")],
            """{"token": "t-1", "user": {"roles": ["admin", "dev"]}}""");
        var resolver = new HttpVariableResolver(
            new Dictionary<string, string>(), responses: name => name == "login" ? login : null);

        Assert.Equal("t-1", resolver.Resolve("{{login.response.body.$.token}}"));
        Assert.Equal("dev", resolver.Resolve("{{login.response.body.$.user.roles[1]}}"));
        Assert.Equal("/users/7", resolver.Resolve("{{login.response.headers.location}}"));
        Assert.Equal("{{other.response.body.$.token}}", resolver.Resolve("{{other.response.body.$.token}}"));
    }

    [Fact]
    public void JsonPathは文字列以外をJSON表記で返す()
    {
        Assert.Equal("""["a","b"]""", JsonPath.Select("""{"x": ["a","b"]}""", "$.x"));
        Assert.Equal("3", JsonPath.Select("""{"x": {"n": 3}}""", "$['x'].n"));
        Assert.Null(JsonPath.Select("""{"x": 1}""", "$.y"));
        Assert.Null(JsonPath.Select("not json", "$.x"));
    }

    [Fact]
    public void DotEnvを読む()
    {
        var env = DotEnvFile.Parse("# c\nA=1\nexport B = \"two words\"\nC='x'\nbroken\n");

        Assert.Equal("1", env["A"]);
        Assert.Equal("two words", env["B"]);
        Assert.Equal("x", env["C"]);
        Assert.Equal(3, env.Count);
    }

    [Fact]
    public void 未定義の変数でURLが組めなければ送らない()
    {
        var block = HttpRequestFile.Parse("GET {{host}}/a").Requests[0];

        var prepared = HttpRequestRunner.Prepare(block, new HttpVariableResolver(new Dictionary<string, string>()), null);

        Assert.NotNull(prepared.Error);
        Assert.Contains("host", prepared.Error);
    }

    [Fact]
    public async Task 送って応答を整形して返す()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":1,"名前":"テスト"}""", Encoding.UTF8, "application/json"),
        });
        using var runner = new HttpRequestRunner(handler);
        var doc = HttpRequestFile.Parse("""
            @host = https://api.example.com
            POST {{host}}/items
            Content-Type: application/json
            X-Trace: abc

            {"name": "x"}
            """);
        var prepared = HttpRequestRunner.Prepare(doc.Requests[0], new HttpVariableResolver(doc.Variables), null);

        var result = await runner.SendAsync(prepared, CancellationToken.None);

        Assert.Equal(201, result.StatusCode);
        Assert.True(result.IsSuccess);
        Assert.Contains("\"名前\": \"テスト\"", result.DisplayBody);   // 日本語を \u にしない
        Assert.Contains("\n", result.DisplayBody);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.example.com/items", sent.Url);
        Assert.Equal("abc", sent.Headers["X-Trace"]);
        Assert.Equal("application/json", sent.Headers["Content-Type"]);
        Assert.Equal("""{"name": "x"}""", sent.Body);
    }

    [Fact]
    public async Task 接続できなければ理由を返す()
    {
        using var runner = new HttpRequestRunner(new RecordingHandler(_ => throw new HttpRequestException("名前を解決できません")));
        var prepared = HttpRequestRunner.Prepare(
            HttpRequestFile.Parse("GET https://nowhere.invalid/").Requests[0],
            new HttpVariableResolver(new Dictionary<string, string>()), null);

        var result = await runner.SendAsync(prepared, CancellationToken.None);

        Assert.Null(result.StatusCode);
        Assert.Equal("送れませんでした", result.StatusLine);
        Assert.Contains("名前を解決できません", result.Error);
    }

    [Fact]
    public void 種類の書かれていない応答は中身でバイナリを見分ける()
    {
        Assert.True(HttpRequestRunner.IsBinary(null, [0x89, 0x50, 0x00, 0x47]));
        Assert.False(HttpRequestRunner.IsBinary(null, Encoding.UTF8.GetBytes("hello")));
        Assert.True(HttpRequestRunner.IsBinary("image/png", Encoding.UTF8.GetBytes("hello")));
        Assert.False(HttpRequestRunner.IsBinary("application/problem+json", [0x00]));
    }

    [Fact]
    public void 同じメソッドとURLが並んでも鍵は分かれる()
    {
        var doc = HttpRequestFile.Parse("GET https://a/\n###\nGET https://a/\n###\n# @name x\nGET https://a/\n");

        var keys = doc.Requests.Select(r => HttpEditorSupport.KeyOf(doc, r)).ToList();

        Assert.Equal(["GET https://a/", "GET https://a/ (2)", "@x"], keys);
    }

    [Fact]
    public void ペグボードへ残す中身はリクエスト行を頭に付ける()
    {
        var request = new PreparedHttpRequest("GET", "https://a/b", [], null, [], null);
        var result = new HttpExchangeResult(request, new DateTimeOffset(2026, 10, 18, 14, 3, 0, TimeSpan.Zero),
            TimeSpan.FromMilliseconds(12), 200, "OK", "HTTP/1.1", [], "application/json", 7,
            """{"a":1}""", "{\n  \"a\": 1\n}", false, false, null);

        var text = HttpRequestPanelViewModel.MaterialText(result);

        Assert.StartsWith("GET https://a/b\nHTTP/1.1 200 OK\n\n{", text.Replace("\r\n", "\n"));
        Assert.Equal("GET https://a/b → 200 OK · 10/18 14:03", HttpRequestPanelViewModel.MaterialTitle(result));
    }

    private sealed record SentRequest(HttpMethod Method, string Url, Dictionary<string, string> Headers, string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new SentRequest(request.Method, request.RequestUri!.ToString(), headers, body));
            return respond(request);
        }
    }
}
