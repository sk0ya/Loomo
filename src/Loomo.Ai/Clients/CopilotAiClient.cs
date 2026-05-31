using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// GitHub Copilot プロバイダ。設定に保存された GitHub トークン(gho_…)を
/// Copilot セッショントークンへ交換し、OpenAI互換の Chat Completions で応答する。
/// GitHub サインインは <see cref="CopilotAuthService"/>（設定画面）で行う。
/// </summary>
public sealed class CopilotAiClient : IAiClient
{
    private const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";
    private const string ChatUrl = "https://api.githubcopilot.com/chat/completions";

    private readonly HttpClient _http;
    private readonly AiSettings _settings;

    // 交換済みセッショントークンを期限まで再利用する
    private string? _sessionToken;
    private DateTimeOffset _sessionExpiry;

    public CopilotAiClient(HttpClient http, AiSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public AiProvider Provider => AiProvider.Copilot;

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _settings.Copilot;
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            yield return new AgentError("GitHub Copilot は未サインインです。設定画面から GitHub でサインインしてください。");
            yield break;
        }

        AgentError? prep = null;
        try
        {
            await EnsureSessionTokenAsync(cfg.ApiKey!, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            prep = new AgentError($"Copilot トークンの取得に失敗しました: {ex.Message}");
        }
        if (prep is not null) { yield return prep; yield break; }

        var model = string.IsNullOrWhiteSpace(cfg.Model) ? "gpt-4o" : cfg.Model;
        var body = OpenAiProtocol.BuildRequest(conversation, tools, model, cfg.MaxTokens, _settings.SystemPrompt);

        await foreach (var ev in OpenAiProtocol.SendAsync(
            _http, ChatUrl, body, "Copilot",
            req =>
            {
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_sessionToken}");
                req.Headers.TryAddWithoutValidation("Editor-Version", "Loomo/1.0");
                req.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
                req.Headers.TryAddWithoutValidation("User-Agent", "Loomo/1.0");
            },
            ct))
        {
            yield return ev;
        }
    }

    /// <summary>GitHub トークンを Copilot セッショントークンへ交換する（期限内はキャッシュ）。</summary>
    private async Task EnsureSessionTokenAsync(string githubToken, CancellationToken ct)
    {
        if (_sessionToken is not null && DateTimeOffset.UtcNow < _sessionExpiry.AddMinutes(-1))
            return;

        using var req = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"token {githubToken}");
        req.Headers.TryAddWithoutValidation("User-Agent", "Loomo/1.0");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)resp.StatusCode}: {json}");

        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("トークン応答が空です。");
        _sessionToken = node["token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Copilot トークンが取得できませんでした（Copilot 契約を確認してください）。");
        var exp = node["expires_at"]?.GetValue<long>()
            ?? DateTimeOffset.UtcNow.AddMinutes(25).ToUnixTimeSeconds();
        _sessionExpiry = DateTimeOffset.FromUnixTimeSeconds(exp);
    }
}
