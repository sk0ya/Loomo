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

    public static readonly AgentProfile ChatUnderstanding = new(
        "chat-understanding",
        "チャット理解",
        SystemPromptSuffix:
        "ステージ: AI1 チャット理解。ユーザー入力を読み、外部確認や作業が必要なら本文で代替せず pwsh tool_use を1件返す。" +
        "ツール不要ならその場で簡潔に回答する。ツール実行結果がまだ無い段階なので、結果を推測して最終回答しない。");

    public static readonly AgentProfile ResultJudge = new(
        "result-judge",
        "結果判断",
        SystemPromptSuffix:
"ステージ: AI3 結果判断。直前の tool 結果を読み、目的が満たされたら日本語で簡潔に最終回答する。" +
            "このステージではツールは使えない。まだ確認や追加作業が必要な場合は、最初の行に [CONTINUE] とだけ書き、" +
            "続けて次に何をすべきかを一文で示す（次の理解ステージが pwsh を実行する）。" +
            "目的が満たされたなら [CONTINUE] は付けず、結果から分かる範囲だけで最終回答を返す。");

    public static readonly AgentProfile ToolExecutor = new(
        "tool-executor",
        "ツール実行",
        SystemPromptSuffix:
"ステージ: AI2 ツール実行。このステージはAIではなく Loomo のC#コードが承認、安全評価、pwsh実行を担当する。");

    public static readonly IReadOnlyList<AgentProfile> ResidentPipeline = new[]
    {
        ChatUnderstanding,
        ResultJudge,
    };

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
