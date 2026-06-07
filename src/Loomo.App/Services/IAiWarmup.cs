using System;

namespace sk0ya.Loomo.App.Services;

/// <summary>AIウォームアップの状態通知・再要求の窓口。ViewModel は具体実装
/// （<see cref="LocalLlmWarmupService"/>・ONNXエンジン依存）ではなくこの抽象に依存する
/// （テストでは副作用のないフェイクに差し替えられる）。</summary>
public interface IAiWarmup
{
    /// <summary>いまウォームアップを実行中か。実行中は AI への指示を受け付けない。</summary>
    bool IsWarmingUp { get; }

    /// <summary>現在のウォームアップが始まった時刻。停止中は null。</summary>
    DateTimeOffset? WarmupStartedAt { get; }

    /// <summary><see cref="IsWarmingUp"/> が変化したときに通知する（開始↔終了の遷移時のみ）。</summary>
    event Action? StateChanged;

    /// <summary>暖機を改めて要求する（無効時・モデル未設定時は何もしない）。</summary>
    void RequestWarmup();
}
