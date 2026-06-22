using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>1回の生成リクエスト。プロンプト（Phi-4 テンプレート適用済み文字列）とサンプリング・長さ制約。</summary>
public sealed record GenerationRequest(
    string Prompt,
    string ModelPath,
    int MaxLength,
    int MaxNewTokens,
    SamplingOptions Sampling);

/// <summary>
/// ローカル推論エンジン抽象。<see cref="OnnxGenAiClient"/> から呼ばれ、生成結果を
/// <see cref="TextDelta"/>（逐次本文）と <see cref="AiUsageReported"/>（トークン数・所要）として
/// チャネルへ流す。失敗時は <see cref="AgentError"/> を流す。常に writer を完了させる。
/// 実体は <see cref="OnnxGenAiEngine"/>（ONNX Runtime GenAI）。テストでは差し替え可能。
/// </summary>
public interface ILocalInferenceEngine
{
    Task GenerateAsync(GenerationRequest request, ChannelWriter<AgentEvent> sink, CancellationToken ct);
}

/// <summary>
/// 事前ロード＋安定プレフィックス（system＋tools）の prefill による暖機に対応するエンジン。ONNX / llama.cpp の
/// どちらの実体も実装し、<see cref="LocalLlmWarmupService"/>（App 層）が modelPath で選んだ方を暖機する。
/// 必ず実ターンと同じ <paramref name="stablePrompt"/>／<paramref name="maxLength"/> で呼ぶこと
/// （プレフィックスを実ターンの最長共通接頭辞に一致させ、初回ターンが KV を再利用できるようにするため）。
/// </summary>
public interface ILocalWarmableEngine
{
    /// <summary>モデルがロード済みで即座に使える状態か。</summary>
    bool IsLoaded { get; }

    /// <summary>モデルをロードし、安定プレフィックスを常駐コンテキストへ prefill して KV を温める。</summary>
    Task PrimeAsync(
        string modelPath, string stablePrompt, int maxLength, SamplingOptions sampling,
        CancellationToken ct, Action<string>? progress = null);
}
