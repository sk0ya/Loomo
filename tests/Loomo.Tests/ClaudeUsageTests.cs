using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services;
using Xunit;

namespace sk0ya.Loomo.Tests;

public class ClaudeUsageTests
{
    private const string SampleResponse = """
        {"five_hour":{"utilization":34.0,"resets_at":"2026-10-05T13:40:00.035969+00:00"},
         "seven_day":{"utilization":98.0,"resets_at":"2026-10-05T16:00:00.035989+00:00"},
         "seven_day_opus":null,"extra_usage":{"is_enabled":false}}
        """;

    [Fact]
    public void Parse_5時間枠と週枠を取り出す()
    {
        var usage = ClaudeUsageClient.Parse(SampleResponse);

        Assert.NotNull(usage);
        Assert.Equal(34.0, usage.FiveHour!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 13, 40, 0, 35, TimeSpan.Zero).AddTicks(9690), usage.FiveHour.ResetsAt);
        Assert.Equal(98.0, usage.SevenDay!.Percent);
    }

    [Fact]
    public void Parse_枠がnullの応答や壊れたJSONはnull()
    {
        Assert.Null(ClaudeUsageClient.Parse("""{"five_hour":null,"seven_day":null}"""));
        Assert.Null(ClaudeUsageClient.Parse("{not json"));
    }

    [Fact]
    public void ReadCredential_無い壊れているならnull_あればトークンと期限()
    {
        var dir = Directory.CreateTempSubdirectory("loomo-claude-usage-");
        try
        {
            var path = Path.Combine(dir.FullName, ".credentials.json");
            Assert.Null(ClaudeUsageClient.ReadCredential(path));

            File.WriteAllText(path, "{ broken");
            Assert.Null(ClaudeUsageClient.ReadCredential(path));

            File.WriteAllText(path, """{"claudeAiOauth":{"accessToken":"tok","expiresAt":1791217748483}}""");
            var credential = ClaudeUsageClient.ReadCredential(path);
            Assert.Equal("tok", credential!.Value.AccessToken);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791217748483), credential.Value.ExpiresAt);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FetchAsync_期限切れトークンでは通信せず更新もしない()
    {
        var dir = Directory.CreateTempSubdirectory("loomo-claude-usage-");
        try
        {
            var path = Path.Combine(dir.FullName, ".credentials.json");
            var expired = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            File.WriteAllText(path, $$$"""{"claudeAiOauth":{"accessToken":"tok","expiresAt":{{{expired}}}}}""");
            var handler = new CountingHandler();
            var client = new ClaudeUsageClient(new HttpClient(handler), path);

            var result = await client.FetchAsync(CancellationToken.None);

            Assert.Equal(ClaudeUsageProblem.TokenExpired, result.Problem);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FetchAsync_資格情報が無ければ未ログイン()
    {
        var client = new ClaudeUsageClient(new HttpClient(new CountingHandler()),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), ".credentials.json"));

        var result = await client.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeUsageProblem.NotSignedIn, result.Problem);
    }

    [Fact]
    public void FormatLabel_5時間枠の割合だけ_無ければ週枠()
    {
        Assert.Equal("35%", ClaudeUsageViewModel.FormatLabel(new(new(34.6, null), new(98, null))));
        Assert.Equal("98%", ClaudeUsageViewModel.FormatLabel(new(null, new(98, null))));
    }

    [Fact]
    public void FormatRemaining_分_時間分_日時間()
    {
        Assert.Equal("19分", ClaudeUsageViewModel.FormatRemaining(TimeSpan.FromMinutes(18.2)));
        Assert.Equal("2時間39分", ClaudeUsageViewModel.FormatRemaining(TimeSpan.FromMinutes(159)));
        Assert.Equal("4日15時間", ClaudeUsageViewModel.FormatRemaining(TimeSpan.FromHours(4 * 24 + 15.5)));
        Assert.Equal("まもなく", ClaudeUsageViewModel.FormatRemaining(TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public void BuildRows_経過線は枠の長さとリセット時刻から出す()
    {
        var now = new DateTimeOffset(2026, 10, 5, 22, 21, 0, TimeSpan.FromHours(9));
        var usage = new ClaudeUsage(new(34, now.AddHours(1)), new(98, now.AddDays(3.5)));

        var rows = ClaudeUsageViewModel.BuildRows(usage, now);

        Assert.Equal(2, rows.Count);
        Assert.Equal("5時間  34%", rows[0].Name);
        Assert.Equal(80, rows[0].ElapsedPercent!.Value, precision: 6);   // 5時間のうち4時間経過
        Assert.Equal("1時間0分", rows[0].Remaining);
        Assert.False(rows[0].IsWarning);
        Assert.Equal(50, rows[1].ElapsedPercent!.Value, precision: 6);
        Assert.True(rows[1].IsWarning);
    }

    [Fact]
    public void BuildRows_リセット時刻が無ければ経過線を出さない()
    {
        var row = Assert.Single(ClaudeUsageViewModel.BuildRows(new(new(10, null), null), DateTimeOffset.Now));
        Assert.False(row.HasElapsed);
        Assert.Equal("", row.Remaining);
    }

    [Fact]
    public void FormatFooter_取得時刻と状態()
    {
        var at = new DateTimeOffset(2026, 10, 5, 22, 21, 0, TimeSpan.FromHours(9));
        Assert.Contains("認証の期限切れ", ClaudeUsageViewModel.FormatFooter(at, ClaudeUsageProblem.TokenExpired));
        Assert.Contains("クリックで更新", ClaudeUsageViewModel.FormatFooter(at, ClaudeUsageProblem.None));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleResponse) });
        }
    }
}
