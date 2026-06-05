using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using sk0ya.Loomo.Core.Tools.Implementations;

namespace sk0ya.Loomo.Core.Safety;

/// <summary>
/// <see cref="SafetySettings"/> に基づく既定の安全ポリシー実装。
/// pwsh（PowerShell 実行）の引数を危険コマンドのブロックリストと照合する。
/// 設定インスタンスを保持し、評価のたびに最新値を読むため設定変更が即時反映される。
/// </summary>
public sealed class SafetyPolicy : ISafetyPolicy
{
    private readonly SafetySettings _settings;

    public SafetyPolicy(SafetySettings settings) => _settings = settings;

    public bool AutoApprove => _settings.AutoApprove;

    public SafetyDecision Evaluate(string toolName, JsonElement arguments)
    {
        if (toolName != "pwsh") return SafetyDecision.Allow;

        // 引数はオーケストレータが NormalizeArguments で canonical 化済み（command キーに正規化される）前提。
        var command = arguments.GetString("command");
        if (string.IsNullOrWhiteSpace(command)) return SafetyDecision.Allow;

        foreach (var pattern in _settings.BlockedCommandPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            Match match;
            try
            {
                match = Regex.Match(command, pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                continue; // 不正な正規表現はスキップ（設定ミスでアプリを止めない）
            }
            if (match.Success)
                return SafetyDecision.Block(
                    $"危険コマンドのブロックリストに一致したため実行を中止しました（パターン: {pattern}）。");
        }

        return SafetyDecision.Allow;
    }
}
