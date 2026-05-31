using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.Ai;

/// <summary>GitHub のデバイス認証（OAuth Device Flow）で Copilot 用の GitHub トークンを取得する。</summary>
public sealed class CopilotAuthService
{
    // GitHub Copilot が用いる公開クライアントID（VS Code / CLI と同じ）。
    private const string ClientId = "Iv1.b507a08c87ecfe98";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";

    private readonly HttpClient _http;

    public CopilotAuthService(HttpClient http) => _http = http;

    /// <summary>デバイスコードを発行する（ユーザーコード・認証URLを含む）。</summary>
    public async Task<DeviceCodeInfo> RequestDeviceCodeAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = "read:user"
            })
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "Loomo");

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"デバイスコード取得失敗 {(int)resp.StatusCode}: {json}");

        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("デバイスコード応答が空です。");
        return new DeviceCodeInfo(
            DeviceCode: node["device_code"]?.GetValue<string>() ?? throw new InvalidOperationException("device_code がありません。"),
            UserCode: node["user_code"]?.GetValue<string>() ?? "",
            VerificationUri: node["verification_uri"]?.GetValue<string>() ?? "https://github.com/login/device",
            Interval: node["interval"]?.GetValue<int>() ?? 5,
            ExpiresIn: node["expires_in"]?.GetValue<int>() ?? 900);
    }

    /// <summary>ユーザーが認証を完了するまでポーリングし、GitHub アクセストークン(gho_…)を返す。</summary>
    public async Task<string> PollForAccessTokenAsync(DeviceCodeInfo info, CancellationToken ct)
    {
        var interval = Math.Max(1, info.Interval);
        var deadline = DateTime.UtcNow.AddSeconds(info.ExpiresIn);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            using var req = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = info.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "Loomo");

            using var resp = await _http.SendAsync(req, ct);
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));

            var token = node?["access_token"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(token)) return token!;

            switch (node?["error"]?.GetValue<string>())
            {
                case "authorization_pending":
                    break;                       // まだ承認待ち
                case "slow_down":
                    interval += 5;               // ポーリング間隔を延ばす
                    break;
                case "expired_token":
                    throw new TimeoutException("デバイスコードの有効期限が切れました。再度サインインしてください。");
                case "access_denied":
                    throw new InvalidOperationException("ユーザーが認証を拒否しました。");
                case { } err:
                    throw new InvalidOperationException($"認証エラー: {err}");
            }
        }
        throw new TimeoutException("デバイス認証がタイムアウトしました。");
    }
}

/// <summary>デバイス認証の初期応答。</summary>
public sealed record DeviceCodeInfo(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int Interval,
    int ExpiresIn);
