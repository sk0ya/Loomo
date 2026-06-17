using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    private readonly OnnxGenAiEngine _engine;
    private readonly IWorkspaceService _workspace;
    private readonly ToolRegistry _tools;
    private readonly CancellationTokenSource _startupCts = new();
    private readonly object _requestLock = new();
    private readonly object _statusLock = new();

    private long _requestSeq;
    private bool _workerRunning;
    private int _activePrimes;
    private long _warmupStartedAtUnixMs;
    private string _currentStatus = "";
    private string _statusDetails = "";
    private readonly Stopwatch _warmupClock = new();
    private readonly Stopwatch _stageClock = new();
    private readonly List<WarmupStageTiming> _stageTimings = new();
    private string? _stageName;
    private TimeSpan? _totalDuration;

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

    /// <summary>ウォームアップ中に現在実行している段階。完了後は直近の完了状態。</summary>
    public string CurrentStatus => Volatile.Read(ref _currentStatus);

    /// <summary>ウォームアップの現在/直近の段階別所要。完了後も次のAIチャット開始まで残る。</summary>
    public string StatusDetails
    {
        get
        {
            lock (_statusLock)
                return IsWarmingUp ? BuildDetailsLocked(includeCurrent: true) : _statusDetails;
        }
    }

    /// <summary>ウォームアップの現在/直近の段階別所要を構造化したもの。</summary>
    public IReadOnlyList<WarmupStageTiming> StageTimings
    {
        get
        {
            lock (_statusLock)
                return BuildTimingsLocked(includeCurrent: IsWarmingUp);
        }
    }

    /// <summary>直近ウォームアップの合計所要。未実行なら null。</summary>
    public TimeSpan? TotalDuration
    {
        get
        {
            lock (_statusLock)
                return IsWarmingUp ? _warmupClock.Elapsed : _totalDuration;
        }
    }

    /// <summary><see cref="IsWarmingUp"/>、<see cref="CurrentStatus"/>、<see cref="StatusDetails"/> が変化したときに通知する。</summary>
    public event Action? StateChanged;

    public LocalLlmWarmupService(
        AiSettings settings, OnnxGenAiEngine engine, IWorkspaceService workspace, ToolRegistry tools)
    {
        _settings = settings;
        _engine = engine;
        _workspace = workspace;
        _tools = tools;

        // ワークスペースが確定／切り替わるたびに、プレフィックスを実ターンと一致させ直す。
        _workspace.RootChanged += OnRootChanged;

        // 起動直後は root 確定通知が続けて来ることがあるため、短く遅延して連続要求をまとめる。
        RequestWarmup();
    }

    private void OnRootChanged(object? sender, string? root) => RequestWarmup();

    /// <summary>ウォームアップを改めて要求する。設定でONに切り替えた直後などに呼ぶ。
    /// 無効時・モデル未設定時は何もしない。</summary>
    public void RequestWarmup()
    {
        if (!_settings.WarmupEnabled)
            return;
        if (string.IsNullOrWhiteSpace(_settings.Local.ModelPath))
            return;

        lock (_requestLock)
        {
            Interlocked.Increment(ref _requestSeq);
            if (_workerRunning) return;
            _workerRunning = true;
        }

        _ = RunPrimeWorkerAsync(_startupCts.Token);
    }

    private async Task RunPrimeWorkerAsync(CancellationToken ct)
    {
        var completedSeq = 0L;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var observed = Interlocked.Read(ref _requestSeq);

                // 起動直後や workspace root 変更直後の連続通知を 1 回の暖機に畳む。
                await Task.Delay(500, ct);
                var afterDelay = Interlocked.Read(ref _requestSeq);
                if (afterDelay != observed)
                    continue;

                await PrimeAsync(ct);
                completedSeq = afterDelay;

                if (Interlocked.Read(ref _requestSeq) == afterDelay)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_requestLock)
            {
                _workerRunning = false;
                if (!ct.IsCancellationRequested && Interlocked.Read(ref _requestSeq) != completedSeq)
                {
                    // 終了処理との隙間に入った要求を拾う。
                    _workerRunning = true;
                    _ = RunPrimeWorkerAsync(ct);
                }
            }
        }
    }

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
            SetStatus("プロンプトを組み立てています");

            // 最初の実ターンと同じ経路（モデルの ChatFormat に応じたフォーマッタ）で安定プレフィックスを
            // 組み立てる。会話は空なので Build は system ブロック＋生成開始マーカを返し、その system ブロックが
            // 実ターン（同じ system ブロック＋ユーザーターン）との最長共通接頭辞になる。
            // profile は対話セッション既定の Root（AgentOrchestrator.RunTurnAsync と一致）。
            var modelProfile = ModelProfiles.Resolve(cfg.Model);
            var prompt = ChatPrompt.Build(
                modelProfile.Format, _settings, AgentProfiles.Root, _workspace.RootPath, new Conversation(), _tools.Definitions);
            var maxLength = ModelProfiles.EffectiveNumCtx(cfg.Model, cfg.NumCtx);
            var sampling = modelProfile.Sampling;

            SetStatus("モデル設定を確認しています");
            await _engine.PrimeAsync(cfg.ModelPath, prompt, maxLength, sampling, ct, SetStatus);
        }
        catch (OperationCanceledException) { }
        catch { /* 暖機は体感改善用。失敗は通常のAI呼び出し時に改めて顕在化する。 */ }
        finally { EndWarmup(); }
    }

    private void SetStatus(string status)
    {
        var changed = false;
        lock (_statusLock)
        {
            if (string.Equals(_currentStatus, status, StringComparison.Ordinal))
                return;

            CompleteCurrentStageLocked();
            _stageName = status;
            _stageClock.Restart();
            _currentStatus = status;
            _statusDetails = BuildDetailsLocked(includeCurrent: false);
            changed = true;
        }

        if (changed) StateChanged?.Invoke();
    }

    // 暖機の開始/終了を計数し、状態が 0↔1 を跨ぐとき（実行中↔停止）だけ通知する。
    private void BeginWarmup()
    {
        if (Interlocked.Increment(ref _activePrimes) == 1)
        {
            Interlocked.Exchange(ref _warmupStartedAtUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            lock (_statusLock)
            {
                _stageTimings.Clear();
                _warmupClock.Restart();
                _stageName = "ウォームアップを開始しています";
                _stageClock.Restart();
                _currentStatus = _stageName;
                _statusDetails = "";
                _totalDuration = null;
            }
            StateChanged?.Invoke();
        }
    }

    private void EndWarmup()
    {
        if (Interlocked.Decrement(ref _activePrimes) == 0)
        {
            Interlocked.Exchange(ref _warmupStartedAtUnixMs, 0);
            lock (_statusLock)
            {
                CompleteCurrentStageLocked();
                _warmupClock.Stop();
                _totalDuration = _warmupClock.Elapsed;
                _currentStatus = "ウォームアップ完了";
                _statusDetails = BuildDetailsLocked(includeCurrent: false);
            }
            StateChanged?.Invoke();
        }
    }

    private void CompleteCurrentStageLocked()
    {
        if (_stageName is null || !_stageClock.IsRunning)
            return;

        _stageClock.Stop();
        if (_stageClock.Elapsed >= TimeSpan.FromMilliseconds(1))
            _stageTimings.Add(new WarmupStageTiming(_stageName, _stageClock.Elapsed));
    }

    private IReadOnlyList<WarmupStageTiming> BuildTimingsLocked(bool includeCurrent)
    {
        var timings = _stageTimings.ToList();
        if (includeCurrent && _stageName is not null && _stageClock.IsRunning)
            timings.Add(new WarmupStageTiming(_stageName, _stageClock.Elapsed));
        return timings;
    }

    private string BuildDetailsLocked(bool includeCurrent)
    {
        var timings = BuildTimingsLocked(includeCurrent);

        if (timings.Count == 0)
            return "";

        var parts = timings
            .Where(t => t.Elapsed >= TimeSpan.FromMilliseconds(1))
            .Select(t => $"{t.Name} {FormatDuration(t.Elapsed)}")
            .ToList();

        if (_warmupClock.Elapsed >= TimeSpan.FromMilliseconds(1))
            parts.Add($"合計 {FormatDuration(_warmupClock.Elapsed)}");

        return string.Join(" / ", parts);
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1)
            return $"{elapsed.TotalMilliseconds:0} ms";
        if (elapsed.TotalMinutes < 1)
            return $"{elapsed.TotalSeconds:0.0} 秒";
        return $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";
    }
    private bool _disposed;

    /// <summary>冪等にする：concrete＋interface の二重 DI 登録で同一インスタンスが2回 Dispose され、
    /// 2回目の Cancel() が ObjectDisposedException で終了時クラッシュになっていた。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _workspace.RootChanged -= OnRootChanged;
        _startupCts.Cancel();
        _startupCts.Dispose();
    }
}
