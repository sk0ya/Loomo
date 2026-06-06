using System;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Tools;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 起動時にローカル推論エンジン（ONNX Runtime GenAI）を暖機し、最初のAIターンの待ち時間を減らす。
/// 単にモデルをロードするだけでなく、<b>最初の実ターンとバイト単位で一致する安定プレフィックス
/// （system プロンプト＋ツール定義）</b>を常駐 Generator へ prefill しておく。これにより
/// (1) 数 GB の重みがページインされ、(2) 初回ターンが KV プレフィックスを再利用して prefill を払い直さない。
///
/// プレフィックスは実ターンと同じ <see cref="Phi4PromptFormatter.Build"/>／<see cref="ModelProfiles"/> で
/// 組み立てる。起動直後はワークスペースルートが未確定なため、確定（<see cref="IWorkspaceService.RootChanged"/>）
/// のたびに同じ要領で貼り直し、プレフィックスを実ターンと一致させ続ける（差分のみ再 prefill されるので安価）。
/// </summary>
public sealed class LocalLlmWarmupService : IDisposable
{
    private readonly AiSettings _settings;
    private readonly Phi4Engine _engine;
    private readonly IWorkspaceService _workspace;
    private readonly ToolRegistry _tools;
    private readonly CancellationTokenSource _startupCts = new();

    public LocalLlmWarmupService(
        AiSettings settings, Phi4Engine engine, IWorkspaceService workspace, ToolRegistry tools)
    {
        _settings = settings;
        _engine = engine;
        _workspace = workspace;
        _tools = tools;

        // ワークスペースが確定／切り替わるたびに、プレフィックスを実ターンと一致させ直す。
        _workspace.RootChanged += OnRootChanged;

        // 起動直後に現在の状態（多くの場合 root 未確定）で一度暖機する。重みのページインはここで前倒しされ、
        // 直後に RootChanged が来れば差分だけ貼り直す。
        _ = PrimeAsync(_startupCts.Token);
    }

    private void OnRootChanged(object? sender, string? root) => _ = PrimeAsync(_startupCts.Token);

    private async Task PrimeAsync(CancellationToken ct)
    {
        try
        {
            var cfg = _settings.Local;
            if (string.IsNullOrWhiteSpace(cfg.ModelPath))
                return;

            // 最初の実ターンと同じ経路で安定プレフィックスを組み立てる。会話は空なので Build は
            // 「<|system|>…<|tool|>…<|/tool|><|end|><|assistant|>」を返し、その system ブロックが
            // 実ターン（同じ system ブロック＋<|user|>…）との最長共通接頭辞になる。
            // profile は対話セッション既定の Root（AgentOrchestrator.RunTurnAsync と一致）。
            var prompt = Phi4PromptFormatter.Build(
                _settings, AgentProfiles.Root, _workspace.RootPath, new Conversation(), _tools.Definitions);
            var maxLength = ModelProfiles.EffectiveNumCtx(cfg.Model, cfg.NumCtx);
            var sampling = ModelProfiles.Resolve(cfg.Model).Sampling;

            await _engine.PrimeAsync(cfg.ModelPath, prompt, maxLength, sampling, ct);
        }
        catch (OperationCanceledException) { }
        catch { /* 暖機は体感改善用。失敗は通常のAI呼び出し時に改めて顕在化する。 */ }
    }

    public void Dispose()
    {
        _workspace.RootChanged -= OnRootChanged;
        _startupCts.Cancel();
        _startupCts.Dispose();
    }
}
