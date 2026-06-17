using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace sk0ya.Loomo.App.Services;

public sealed record WarmupStageTiming(string Name, TimeSpan Elapsed);

/// <summary>AIウォームアップの状態通知・再要求の窓口。ViewModel は具体実装
/// （<see cref="LocalLlmWarmupService"/>・ONNXエンジン依存）ではなくこの抽象に依存する
/// （テストでは副作用のないフェイクに差し替えられる）。</summary>
public interface IAiWarmup
{
    /// <summary>いまウォームアップを実行中か。実行中は AI への指示を受け付けない。</summary>
    bool IsWarmingUp { get; }

    /// <summary>現在のウォームアップが始まった時刻。停止中は null。</summary>
    DateTimeOffset? WarmupStartedAt { get; }

    /// <summary>ウォームアップ中に現在実行している段階。完了後は直近の完了状態。</summary>
    string CurrentStatus { get; }

    /// <summary>ウォームアップの現在/直近の段階別所要。完了後も次のAIチャット開始まで残る。</summary>
    string StatusDetails { get; }

    /// <summary>ウォームアップの現在/直近の段階別所要を構造化したもの。</summary>
    IReadOnlyList<WarmupStageTiming> StageTimings { get; }

    /// <summary>直近ウォームアップの合計所要。未実行なら null。</summary>
    TimeSpan? TotalDuration { get; }

    /// <summary><see cref="IsWarmingUp"/>、<see cref="CurrentStatus"/>、<see cref="StatusDetails"/> が変化したときに通知する。</summary>
    event Action? StateChanged;

    /// <summary>暖機を改めて要求する（無効時・モデル未設定時は何もしない）。</summary>
    void RequestWarmup();

    /// <summary>モデルがまだロードされていなければ、その場でウォームアップして完了まで待つ
    /// （チャット送信の直前に呼ぶ）。ロード済みなら即座に返る。<see cref="IsWarmingUp"/> 等の
    /// 状態通知を伴うため、待っている間 UI に進捗を出せる。<c>WarmupEnabled</c> 設定には依らない
    /// （無効でも、初回ターンで避けられないモデルロードに進捗表示を付けるための経路）。</summary>
    Task EnsureWarmAsync(CancellationToken ct);
}
