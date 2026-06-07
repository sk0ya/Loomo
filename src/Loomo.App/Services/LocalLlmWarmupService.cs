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
public sealed class LocalLlmWarmupService : IDisposable, IAiWarmup
{
    private readonly AiSettings _settings;
    private readonly Phi4Engine _engine;
    private readonly IWorkspaceService _workspace;
    private readonly ToolRegistry _tools;
    private readonly CancellationTokenSource _startupCts = new();

    // 実行中の暖機（prime）件数。起動時とワークスペース確定が重なると複数同時に走り得るため計数で持つ。
    private int _activePrimes;
    private long _warmupStartedAtUnixMs;

    /// <summary>いまウォームアップを実行中か。実行中は AI への指示を受け付けないよう UI で使う。</summary>
    public bool IsWarmingUp => Volatile.Read(ref _activePrimes) > 0;

    /// <summary>現在のウォームアップが始まった時刻。停止中は null。</summary>
    public DateTimeOffset? WarmupStartedAt
    {
        get
        {
            var unixMs = Interlocked.Read(ref _warmupStartedAtUnixMs);
            return unixMs == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        }
    }

    /// <summary><see cref="IsWarmingUp"/> が変化したときに通知する（開始↔終了の遷移時のみ）。</summary>
    public event Action? StateChanged;

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

    /// <summary>ウォームアップを改めて要求する。設定でONに切り替えた直後などに呼ぶ。
    /// 無効時・モデル未設定時は何もしない。</summary>
    public void RequestWarmup() => _ = PrimeAsync(_startupCts.Token);

    private async Task PrimeAsync(CancellationToken ct)
    {
        // ウォームアップが無効なら事前ロードしない（最初のAIターンで通常どおりロード／prefill する）。
        if (!_settings.WarmupEnabled)
            return;

        var cfg = _settings.Local;
        if (string.IsNullOrWhiteSpace(cfg.ModelPath))
            return;

        BeginWarmup();
        try
        {
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
        finally { EndWarmup(); }
    }

    // 暖機の開始/終了を計数し、状態が 0↔1 を跨ぐとき（実行中↔停止）だけ通知する。
    private void BeginWarmup()
    {
        if (Interlocked.Increment(ref _activePrimes) == 1)
        {
            Interlocked.Exchange(ref _warmupStartedAtUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            StateChanged?.Invoke();
        }
    }

    private void EndWarmup()
    {
        if (Interlocked.Decrement(ref _activePrimes) == 0)
        {
            Interlocked.Exchange(ref _warmupStartedAtUnixMs, 0);
            StateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        _workspace.RootChanged -= OnRootChanged;
        _startupCts.Cancel();
        _startupCts.Dispose();
    }
}
