using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// Ollama ネイティブ API（<c>/api/chat</c>）を使うローカルLLMクライアント。
/// thinking モデルは <c>think</c>（真偽値）で確実に制御でき、思考は本文と分離して返る。
/// </summary>
public sealed class OllamaClient : IAiClient
{
    private readonly HttpClient _http;
    private readonly AiSettings _settings;
    private readonly IWorkspaceService _workspace;

    public OllamaClient(HttpClient http, AiSettings settings, IWorkspaceService workspace)
    {
        _http = http;
        _settings = settings;
        _workspace = workspace;
    }

    public AiProvider Provider => AiProvider.Local;

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _settings.Local;
        var profile = ModelProfiles.Resolve(cfg.Model);
        var wantThink = cfg.Thinking;
        var host = OllamaLauncher.ResolveHost(cfg.BaseUrl);
        var endpoint = $"{host}/api/chat";
        void Authorize(HttpRequestMessage req)
        {
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cfg.ApiKey}");
        }

        // ローカルLLM は未起動なら Ollama の起動を試みる（手動起動を不要にする）。
        await OllamaLauncher.EnsureRunningAsync(_http, host, ct);

        // システムプロンプトは会話を通じて安定させる（ユーザー設定＋モデル固有のスタイル指示＋
        // 検索ガイダンス）。Ollama は system＋tools の巨大プレフィックスの KV キャッシュを再利用するため、
        // ここに毎ターン変わる内容を混ぜると prefill（CPU 実行では支配的・約40秒）を毎回払い直す。
        // 検索ガイダンスは rg の有無で決まる「環境固定」の値なのでプレフィックスに含めて差し支えない。
        var systemPrompt = _settings.SystemPrompt + profile.StyleGuidance + SearchGuidance(_workspace.RootPath);

        // 「現在のフォルダ」情報は毎ターン変わり得るので、安定プレフィックスではなく最新メッセージ末尾へ添える。
        var workspaceContext = WorkspaceContext.Describe(_workspace);

        System.Text.Json.Nodes.JsonObject Build(bool includeTools) => OllamaProtocol.BuildRequest(
            conversation, tools, cfg.Model, cfg.MaxTokens, systemPrompt, includeTools, wantThink, cfg.NumCtx,
            workspaceContext);

        // プロファイルが tools 非対応とするモデルには最初からツールを送らない（無駄な往復を避ける）。
        // 未知モデルで誤って送ってしまった場合のみ、先頭イベントの「ツール非対応」エラーを見て
        // includeTools:false で再送する（ストリームは貯め込めないので最初の1件だけ覗いて判定する）。
        var sentTools = profile.SupportsTools && tools.Count > 0;
        var fellBack = false;

        await using (var en = OllamaProtocol.SendChatAsync(
                _http, endpoint, Build(includeTools: true), Provider.ToString(), Authorize, ct)
            .GetAsyncEnumerator(ct))
        {
            if (await en.MoveNextAsync())
            {
                if (sentTools && en.Current is AgentError err && IsOllamaToolsUnsupportedError(err.Message))
                {
                    fellBack = true;
                }
                else
                {
                    yield return en.Current;
                    while (await en.MoveNextAsync())
                        yield return en.Current;
                }
            }
        }

        if (fellBack)
        {
            await foreach (var ev in OllamaProtocol.SendChatAsync(
                _http, endpoint, Build(includeTools: false), Provider.ToString(), Authorize, ct))
                yield return ev;
        }
    }

    /// <summary>
    /// 検索手段のガイダンスを実行環境に応じて返す。rg（ripgrep）があればそれを最優先に使わせ、
    /// 無ければ PowerShell 標準の Select-String を案内する。ワークスペースで rg が使えるかの判定
    /// （<see cref="EnvironmentProbe.HasRipgrep"/>）に基づきシステムプロンプトを動的に切り替える。
    /// </summary>
    private static string SearchGuidance(string? workspaceRoot)
        => EnvironmentProbe.HasRipgrep(workspaceRoot)
            ? "\n\n検索: rg を優先。例: rg \"<語>\" <パス>、rg --files <パス>。"
            : "\n\n検索: Select-String を使う。例: Select-String -Pattern \"<語>\" -Path <パス>。";

    private static bool IsOllamaToolsUnsupportedError(string message)
        => message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase)
           || message.Contains("does not support tool", StringComparison.OrdinalIgnoreCase);
}
