using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// ONNX Runtime GenAI（<see cref="ILocalInferenceEngine"/> 実体は <see cref="Phi4Engine"/>）を使う
/// ローカルLLMクライアント。Ollama HTTP クライアントの置き換え。
/// プロンプトを <see cref="Phi4PromptFormatter"/> で組み立ててエンジンへ渡し、本文をバッファして
/// 終端でツール呼び出しを判定する（小型モデルは構造化 tool_calls を出さず本文に JSON 配列で書くため、
/// <see cref="ToolCallTextParser"/> で復元する）。本文判定は旧 Ollama クライアントの終端処理と同型。
/// </summary>
public sealed class OnnxGenAiClient : IAiClient
{
    private readonly ILocalInferenceEngine _engine;
    private readonly AiSettings _settings;
    private readonly IWorkspaceService _workspace;

    public OnnxGenAiClient(ILocalInferenceEngine engine, AiSettings settings, IWorkspaceService workspace)
    {
        _engine = engine;
        _settings = settings;
        _workspace = workspace;
    }

    public AiProvider Provider => AiProvider.Local;

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        Conversation conversation,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct,
        AgentProfile? profile = null)
    {
        var cfg = _settings.Local;
        var modelProfile = ModelProfiles.Resolve(cfg.Model);
        var prompt = Phi4PromptFormatter.Build(_settings, profile, _workspace.RootPath, conversation, tools);
        var maxLength = ModelProfiles.EffectiveNumCtx(cfg.Model, cfg.NumCtx);
        var maxNewTokens = modelProfile.MaxOutputTokens > 0
            ? Math.Min(cfg.MaxTokens, modelProfile.MaxOutputTokens)
            : cfg.MaxTokens;
        var request = new GenerationRequest(prompt, cfg.ModelPath ?? "", maxLength, maxNewTokens, modelProfile.Sampling);

        var channel = Channel.CreateUnbounded<AgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var genTask = _engine.GenerateAsync(request, channel.Writer, ct);

        var finalText = new StringBuilder();
        var sawText = false;
        AgentError? error = null;

        await foreach (var ev in channel.Reader.ReadAllAsync(ct))
        {
            switch (ev)
            {
                case TextDelta td:
                    // 本文はバッファし、終端でツール呼び出しか通常テキストかを判定してから確定出力する。
                    // ただし生成中の様子は見えるよう、生のチャンクを揮発性の RawTextDelta として先に流す
                    // （履歴には積まれず、UI の進捗プレビュー専用）。
                    sawText = true;
                    finalText.Append(td.Text);
                    yield return new RawTextDelta(td.Text);
                    break;
                case AiUsageReported usage:
                    yield return usage;     // 利用統計はそのまま通知（記録目的）
                    break;
                case AgentError e:
                    error = e;
                    break;
                default:
                    yield return ev;        // 念のため他イベントは素通し
                    break;
            }
        }

        await genTask;      // 例外・後始末の観測

        if (error is not null)
        {
            yield return error;
            yield break;
        }

        var text = finalText.ToString();
        var toolCalls = ToolCallTextParser.Parse(text);

        foreach (var tc in toolCalls)
            yield return new ToolUseRequested(tc);

        if (!sawText && toolCalls.Count == 0)
        {
            yield return new AgentError(
                "ローカルモデルから応答本文が返りませんでした。モデルフォルダの指定とモデルの整合性を確認してください。");
            yield break;
        }

        if (toolCalls.Count == 0)
        {
            // ツール呼び出しらしき本文なのに 1 件も解釈できなかった＝不正な JSON。生の JSON を最終回答として
            // 黙って出す（実行されず "何も起きない" ように見える）のではなく、生出力ごと差し戻して
            // オーケストレータに再試行させる（AI が自己修正できる／UI が何を出したか見られる）。
            if (LooksLikeToolCallAttempt(text))
            {
                yield return new ToolCallParseFailed(text);
                yield break;
            }
            if (!string.IsNullOrEmpty(text))
                yield return new TextDelta(text);
            yield return new TurnCompleted(text);
        }
    }

    /// <summary>本文がツール呼び出しの試みに見えるか（JSON 配列／オブジェクトで始まり tool call のキーを含む）。
    /// パース不能でも生 JSON を回答として出さず、誤りを返して再試行させるための判定。</summary>
    private static bool LooksLikeToolCallAttempt(string text)
    {
        var t = text.TrimStart();
        if (t.Length == 0 || (t[0] != '[' && t[0] != '{')) return false;
        return text.Contains("\"name\"") || text.Contains("\"arguments\"") || text.Contains("\"command\"");
    }
}
