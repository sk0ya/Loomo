using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Ai.Clients;

namespace sk0ya.Loomo.Ai;

/// <summary>設定に応じて IAiClient を解決するファクトリ。
/// ローカル推論は ONNX Runtime GenAI（<see cref="ILocalInferenceEngine"/>）を使う in-process クライアント。</summary>
public sealed class AiClientFactory : IAiClientFactory
{
    private readonly ILocalInferenceEngine _engine;
    private readonly AiSettings _settings;
    private readonly IWorkspaceService _workspace;

    public AiClientFactory(ILocalInferenceEngine engine, AiSettings settings, IWorkspaceService workspace)
    {
        _engine = engine;
        _settings = settings;
        _workspace = workspace;
    }

    public IAiClient ResolveCurrent() => Resolve(_settings.Provider);

    public IAiClient Resolve(AiProvider provider) =>
        new OnnxGenAiClient(_engine, _settings, _workspace);
}
