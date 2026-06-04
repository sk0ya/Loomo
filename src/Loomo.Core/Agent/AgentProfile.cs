namespace sk0ya.Loomo.Core.Agent;

/// <summary>
/// 1回のエージェント実行に使う役割プロファイル。
/// UI上のセッション履歴とは別に、Planner / Coder / Reviewer などを短い会話で独立実行するための単位。
/// </summary>
public sealed record AgentProfile(
    string Id,
    string DisplayName,
    string? SystemPromptOverride = null,
    string? SystemPromptSuffix = null)
{
    public string ApplyTo(string baseSystemPrompt)
    {
        var prompt = string.IsNullOrWhiteSpace(SystemPromptOverride)
            ? baseSystemPrompt
            : SystemPromptOverride.Trim();

        return string.IsNullOrWhiteSpace(SystemPromptSuffix)
            ? prompt
            : prompt + "\n\n" + SystemPromptSuffix.Trim();
    }
}

/// <summary>組み込みの役割プロファイル。必要最小限の短い追加指示だけを持つ。</summary>
public static class AgentProfiles
{
    public static readonly AgentProfile Root = new("root", "エージェント");

    public static readonly AgentProfile Planner = new(
        "planner",
        "Planner",
        SystemPromptSuffix:
            "役割: 実装前の計画担当。必要な確認だけを行い、実装・テスト・リスクを短く整理する。ファイル編集はしない。");

    public static readonly AgentProfile Coder = new(
        "coder",
        "Coder",
        SystemPromptSuffix:
            "役割: 実装担当。対象を絞って変更し、既存の設計とスタイルに合わせる。無関係なリファクタは避ける。");

    public static readonly AgentProfile Reviewer = new(
        "reviewer",
        "Reviewer",
        SystemPromptSuffix:
            "役割: レビュー担当。バグ、回帰、テスト不足を優先して指摘する。変更は行わず、根拠となるファイル位置を示す。");

    public static readonly AgentProfile Tester = new(
        "tester",
        "Tester",
        SystemPromptSuffix:
            "役割: 検証担当。指定されたテストやビルドを実行し、失敗時は最小限の原因候補と再現情報を返す。");
}
