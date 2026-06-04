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

            foreach (var profile in AgentProfiles.ResidentPipeline)
            {
                var systemPrompt = OllamaPromptBuilder.Build(_settings, profile, _workspace.RootPath);
                var tools = profile.Id == AgentProfiles.ResultJudge.Id
                    ? Array.Empty<sk0ya.Loomo.Core.Tools.ToolDefinition>()
                    : _tools.Definitions;
                await OllamaLauncher.WarmChatPrefixAsync(
                    http,
                    OllamaLauncher.Host,
                    cfg.Model,
                    systemPrompt,
                    tools,
                    numCtx,
                    cts.Token);
            }
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
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
