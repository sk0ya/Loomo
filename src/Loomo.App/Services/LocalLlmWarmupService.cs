using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// ワークスペース開始時にローカルLLMを先に起動して、最初のAIターンの待ち時間を減らす。
/// </summary>
public sealed class LocalLlmWarmupService : IDisposable
{
    private readonly IWorkspaceService _workspace;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AiSettings _settings;
    private readonly ToolRegistry _tools;
    private CancellationTokenSource? _cts;
    private readonly CancellationTokenSource _startupCts = new();

    public LocalLlmWarmupService(
        IWorkspaceService workspace,
        IHttpClientFactory httpFactory,
        AiSettings settings,
        ToolRegistry tools)
    {
        _workspace = workspace;
        _httpFactory = httpFactory;
        _settings = settings;
        _tools = tools;
        _workspace.RootChanged += OnRootChanged;

        // 最大のコールド要因はモデル重みのページイン（CPU実行で数十秒）。これはワークスペースに
        // 依存しないので、ルート確定（RootChanged）を待たず起動直後に前倒しでロードしておく。
        // keep_alive 無期限と併せ、コールドを「初回1回だけ」に抑えて2回目以降の起動を即ウォームにする。
        _ = WarmModelAtStartupAsync(_startupCts.Token);

        // ルートが既に確定済みなら（復元が購読前に走った場合の保険）プレフィックスも温める。
        if (!string.IsNullOrWhiteSpace(_workspace.RootPath))
            OnRootChanged(this, _workspace.RootPath);
    }

    private async Task WarmModelAtStartupAsync(CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient("ai");
            await OllamaLauncher.EnsureRunningAsync(http, OllamaLauncher.Host, ct, timeout: TimeSpan.FromSeconds(5));
            var cfg = _settings.Local;
            var numCtx = ModelProfiles.EffectiveNumCtx(cfg.Model, cfg.NumCtx);
            await OllamaLauncher.WarmModelAsync(http, OllamaLauncher.Host, cfg.Model, numCtx, ct);
        }
        catch (OperationCanceledException) { }
        catch { /* ウォームアップは体感改善用。失敗しても通常のAI呼び出しで改めて確認する。 */ }
    }

    private void OnRootChanged(object? sender, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _ = WarmAsync(cts);
    }

    private async Task WarmAsync(CancellationTokenSource cts)
    {
        try
        {
            var http = _httpFactory.CreateClient("ai");
            await OllamaLauncher.EnsureRunningAsync(
                http,
                OllamaLauncher.Host,
                cts.Token,
                timeout: TimeSpan.FromSeconds(5));

            var cfg = _settings.Local;
            var numCtx = ModelProfiles.EffectiveNumCtx(cfg.Model, cfg.NumCtx);
            await OllamaLauncher.WarmModelAsync(
                http,
                OllamaLauncher.Host,
                cfg.Model,
                numCtx,
                cts.Token);

            // 単段ループと同じ安定プレフィックス（Root の system ＋ 全ツール）を温めて、
            // 最初のターンの prefill を前倒しする。
            var systemPrompt = OllamaPromptBuilder.Build(_settings, AgentProfiles.Root, _workspace.RootPath);
            await OllamaLauncher.WarmChatPrefixAsync(
                http,
                OllamaLauncher.Host,
                cfg.Model,
                systemPrompt,
                _tools.Definitions,
                numCtx,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // ウォームアップは体感改善用。失敗しても通常のAI呼び出し時に改めて確認する。
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
                _cts = null;
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        _workspace.RootChanged -= OnRootChanged;
        _startupCts.Cancel();
        _startupCts.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
