using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai.Http;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Ai;

/// <summary>
/// Ollama のネイティブ API から利用可能なモデル一覧を取得する。
/// <c>GET {host}/api/tags</c> を叩き、設定画面のモデル選択肢として提示する。
/// </summary>
public sealed class ModelCatalogService
{
    private readonly HttpClient _http;
    private readonly AiSettings _settings;

    public ModelCatalogService(HttpClient http, AiSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>モデル一覧取得に対応するプロバイダか。</summary>
    public static bool Supports(AiProvider provider) =>
        provider is AiProvider.Local;

    /// <summary>
    /// 指定プロバイダのエンドポイントからモデルIDの一覧を取得する。
    /// <paramref name="baseUrlOverride"/> / <paramref name="apiKeyOverride"/> を渡すと、
    /// 設定へ未保存の編集中の値で取得できる（保存前のプレビュー用）。
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchAsync(
        AiProvider provider,
        string? baseUrlOverride = null,
        string? apiKeyOverride = null,
        CancellationToken ct = default)
    {
        if (!Supports(provider))
            throw new NotSupportedException($"{provider} はモデル一覧取得に対応していません。");

        var cfg = _settings.Local;
        var rawBase = !string.IsNullOrWhiteSpace(baseUrlOverride) ? baseUrlOverride : cfg.BaseUrl;
        var host = OllamaLauncher.ResolveHost(rawBase);
        var apiKey = !string.IsNullOrWhiteSpace(apiKeyOverride) ? apiKeyOverride : cfg.ApiKey;

        // 未起動なら Ollama の起動を試みてから取得する（手動起動を不要にする）。
        await OllamaLauncher.EnsureRunningAsync(_http, host, ct);

        using var resp = await HttpRetry.SendAsync(_http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{host}/api/tags");
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            return req;
        }, ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"モデル一覧の取得に失敗しました（{(int)resp.StatusCode}）: {json}");

        var root = JsonNode.Parse(json);
        // Ollama: { "models": [ { "name": "qwen3:4b", ... }, ... ] }
        var list = root?["models"]?.AsArray();
        if (list is null)
            return Array.Empty<string>();

        return list
            .Select(n => n?["name"]?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => Phi4MiniRank(name))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int Phi4MiniRank(string name)
        => name.StartsWith("phi4-mini", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
}
