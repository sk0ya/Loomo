namespace sk0ya.Loomo.Core.Observability;

/// <summary>
/// AI操作トレース（観測性・設計書 §20）の設定。アプリ設定の一部として永続化する。
/// トレースはプロンプト・ファイル内容・コマンド出力を含む機微データのため、オプトアウト可能（§20.7）。
/// </summary>
public sealed class ObservabilitySettings
{
    /// <summary>AI操作トレースを JSONL に記録するか。既定は有効（無効化でオプトアウト）。</summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>保持するトレースファイル(セッション)数の上限。超過分は古い順に削除。0以下で無制限。</summary>
    public int MaxSessions { get; set; } = 200;
}
