using System;
using sk0ya.Loomo.Ai.Clients;
using sk0ya.Loomo.Core.Agent;
using sk0ya.Loomo.Core.Models;

namespace sk0ya.Loomo.Ai;

/// <summary>
/// 現在のプロバイダ設定（<see cref="ProviderConfig.MaxContextTokens"/>）に基づき会話をトリムする
/// <see cref="IContextWindowPolicy"/> 実装。Core はモデル別の予算を知らないため、この橋渡しを Ai 層に置く。
///
/// 入力に使える予算 = コンテキスト上限 − 出力上限(MaxTokens) − システムプロンプト − 安全マージン。
/// </summary>
public sealed class SettingsContextWindowPolicy : IContextWindowPolicy
{
    /// <summary>ツール定義スキーマ等、見積もりに含めていない要素のための安全マージン。</summary>
    private const int SafetyMarginTokens = 2_000;

    private readonly AiSettings _settings;

    public SettingsContextWindowPolicy(AiSettings settings) => _settings = settings;

    public Conversation Fit(Conversation conversation)
    {
        var cfg = _settings.ConfigFor(_settings.Provider);

        // 実際にモデルへ渡せるのは num_ctx までなので、トリム上限を num_ctx と MaxContextTokens の小さい方に揃える。
        // これを超えるとモデル側でサイレントに古いトークンが切り捨てられ、文脈が黙って失われる。
        var contextLimit = cfg.MaxContextTokens;
        var modelWindow = ModelProfiles.EffectiveNumCtx(cfg.Model, cfg.NumCtx);
        if (modelWindow > 0)
            contextLimit = contextLimit > 0 ? Math.Min(contextLimit, modelWindow) : modelWindow;
        if (contextLimit <= 0)
            return conversation;

        var systemTokens = TokenEstimator.EstimateText(_settings.SystemPrompt);
        var inputBudget = contextLimit - cfg.MaxTokens - systemTokens - SafetyMarginTokens;
        if (inputBudget <= 0)
            return conversation; // 設定が矛盾している場合はトリムせず素通し（API側のエラーに委ねる）

        var trimmed = ConversationTrimmer.Trim(conversation.Messages, inputBudget);
        return trimmed.Count == conversation.Messages.Count
            ? conversation
            : ConversationView.FromMessages(trimmed);
    }
}
