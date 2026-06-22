using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// <see cref="ILocalInferenceEngine"/> の振り分け器。<see cref="GenerationRequest.ModelPath"/> から
/// バックエンドを推論する：拡張子 <c>.gguf</c> のファイル → llama.cpp（<see cref="LlamaCppEngine"/>）、
/// それ以外（<c>genai_config.json</c> を持つフォルダ）→ ONNX Runtime GenAI（<see cref="OnnxGenAiEngine"/>）。
/// 新しい設定項目や UI を増やさず、modelPath を切り替えるだけでバックエンドが決まる。
/// 両エンジンはモデルを常駐させるため、ここでは両方を保持し、選んだ方へそのまま委譲する。
/// </summary>
public sealed class LocalInferenceRouter : ILocalInferenceEngine, IDisposable
{
    private readonly OnnxGenAiEngine _onnx;
    private readonly LlamaCppEngine _llama;

    public LocalInferenceRouter(OnnxGenAiEngine onnx, LlamaCppEngine llama)
    {
        _onnx = onnx;
        _llama = llama;
    }

    public Task GenerateAsync(GenerationRequest request, ChannelWriter<AgentEvent> sink, CancellationToken ct)
        => Select(request.ModelPath).GenerateAsync(request, sink, ct);

    private ILocalInferenceEngine Select(string modelPath)
        => LlamaCppEngine.CanHandle(modelPath) ? _llama : _onnx;

    /// <summary>暖機（ロード＋安定プレフィックス prefill）の対象エンジンを modelPath で選ぶ。
    /// <see cref="LocalLlmWarmupService"/> がこれで GGUF→llama.cpp／フォルダ→ONNX を暖機する。</summary>
    public ILocalWarmableEngine WarmableFor(string modelPath)
        => LlamaCppEngine.CanHandle(modelPath) ? _llama : _onnx;

    public void Dispose()
    {
        _llama.Dispose();
        _onnx.Dispose();
    }
}
