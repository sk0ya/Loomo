using System.Text.Json;

namespace sk0ya.Loomo.Core.Safety;

/// <summary>
/// ツール実行前の安全評価（設計書 §10）。エージェントループはツール実行前にこれを問い合わせ、
/// ブロック対象は実行せず、自動承認が有効なら承認カードをスキップする。
/// </summary>
public interface ISafetyPolicy
{
    /// <summary>自動承認が有効か（承認カードを出さずに実行してよいか）。</summary>
    bool AutoApprove { get; }

    /// <summary>このツール呼び出しをブロックすべきか評価する。</summary>
    SafetyDecision Evaluate(string toolName, JsonElement arguments);
}

/// <summary>安全評価の結果。</summary>
public sealed record SafetyDecision(bool Blocked, string? Reason)
{
    public static readonly SafetyDecision Allow = new(false, null);
    public static SafetyDecision Block(string reason) => new(true, reason);
}
