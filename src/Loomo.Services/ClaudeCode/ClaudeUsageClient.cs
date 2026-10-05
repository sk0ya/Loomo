using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Services;

/// <summary>使用枠1つぶん（5時間枠／週枠）。<paramref name="Percent"/> は 0–100。</summary>
public sealed record ClaudeUsageWindow(double Percent, DateTimeOffset? ResetsAt);

/// <summary>Claude Code（Claude.ai サブスクリプション）の使用状況。</summary>
public sealed record ClaudeUsage(ClaudeUsageWindow? FiveHour, ClaudeUsageWindow? SevenDay);

/// <summary>取得の結果。値が取れなかったときは <see cref="Usage"/> が null で、理由を <see cref="Problem"/> に持つ。</summary>
public sealed record ClaudeUsageResult(ClaudeUsage? Usage, ClaudeUsageProblem Problem);

public enum ClaudeUsageProblem
{
    None,
    /// <summary>Claude Code にログインしていない（資格情報ファイルが無い）。表示自体を出さない。</summary>
    NotSignedIn,
    /// <summary>トークンの期限切れ。Loomo は更新しない——Claude Code を使えば Claude Code が更新する。</summary>
    TokenExpired,
    /// <summary>取得の制限（429）。この API は同じトークンでの連続取得をかなり厳しく断るので、間隔を空けて待つ。</summary>
    RateLimited,
    /// <summary>通信・応答の失敗（一時的）。</summary>
    Failed,
}

/// <summary>
/// Claude Code の使用量（<c>/usage</c> と同じ値）を、Claude Code 自身の OAuth 資格情報
/// （<c>~/.claude/.credentials.json</c>）で <c>api/oauth/usage</c> から読む。
/// <b>トークンの更新はしない</b>：リフレッシュトークンはローテーションするので、Loomo が回すと
/// Claude Code 側の手持ちが無効になりログアウトさせてしまう。期限切れなら読み取りを諦めて待つ。
/// </summary>
public sealed class ClaudeUsageClient
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    private readonly HttpClient _http;
    private readonly string _credentialsPath;

    public ClaudeUsageClient(HttpClient? http = null, string? credentialsPath = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _credentialsPath = credentialsPath ?? DefaultCredentialsPath();
    }

    /// <summary>Claude Code の資格情報ファイル。<c>CLAUDE_CONFIG_DIR</c> があればそちら。</summary>
    public static string DefaultCredentialsPath()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDir))
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(configDir, ".credentials.json");
    }

    public async Task<ClaudeUsageResult> FetchAsync(CancellationToken cancellationToken)
    {
        var credential = ReadCredential(_credentialsPath);
        if (credential is null) return new(null, ClaudeUsageProblem.NotSignedIn);
        if (credential.Value.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
            return new(null, ClaudeUsageProblem.TokenExpired);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value.AccessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(null, ClaudeUsageProblem.TokenExpired);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new(null, ClaudeUsageProblem.RateLimited);
            if (!response.IsSuccessStatusCode) return new(null, ClaudeUsageProblem.Failed);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var usage = Parse(json);
            return usage is null ? new(null, ClaudeUsageProblem.Failed) : new(usage, ClaudeUsageProblem.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(null, ClaudeUsageProblem.Failed);
        }
    }

    /// <summary>資格情報ファイルからアクセストークンと期限を読む。無い・壊れているなら null。</summary>
    public static (string AccessToken, DateTimeOffset? ExpiresAt)? ReadCredential(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // Claude Code が書き換え中でも読めるよう共有で開く。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object) return null;
            if (!oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String) return null;
            var accessToken = token.GetString();
            if (string.IsNullOrEmpty(accessToken)) return null;
            DateTimeOffset? expiresAt = oauth.TryGetProperty("expiresAt", out var exp) && exp.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
            return (accessToken, expiresAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary><c>api/oauth/usage</c> の応答から 5時間枠と週枠を取り出す。どちらも無ければ null。</summary>
    public static ClaudeUsage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var fiveHour = ReadWindow(root, "five_hour");
            var sevenDay = ReadWindow(root, "seven_day");
            return fiveHour is null && sevenDay is null ? null : new ClaudeUsage(fiveHour, sevenDay);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ClaudeUsageWindow? ReadWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return null;
        if (!window.TryGetProperty("utilization", out var u) || !u.TryGetDouble(out var percent)) return null;
        DateTimeOffset? resetsAt = window.TryGetProperty("resets_at", out var r)
            && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out var parsed)
                ? parsed
                : null;
        return new ClaudeUsageWindow(Math.Clamp(percent, 0, 100), resetsAt);
    }
}
