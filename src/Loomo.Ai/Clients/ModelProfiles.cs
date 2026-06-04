namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// モデル名から <see cref="ModelProfile"/> を解決する。ファミリ名（タグ ":xxx" を含む接頭辞）で判定し、
/// 現在インストールされている各モデルに合わせて tools / thinking / サンプリングを最適化する。
/// 既知でないモデルは安全側の既定（tools は試行、thinking なし、控えめな num_ctx）にフォールバックする。
/// </summary>
public static class ModelProfiles
{
    /// <summary>
    /// qwen3 系（qwen3:0.6b / 1.7b / 4b）。tools・thinking とも対応。
    /// 公式推奨は thinking 時 temp0.6 / top_p0.95、非 thinking 時 temp0.7 / top_p0.8、共に top_k20 / min_p0。
    /// num_ctx は最小サイズ（0.6b/1.7b）のネイティブ上限 40960 内に収まる 32768 とする。
    /// </summary>
    public static readonly ModelProfile Qwen3 = new()
    {
        Family = "qwen3",
        SupportsTools = true,
        SupportsThinking = true,
        NumCtx = 32768,
        Sampling = new(Temperature: 0.6, TopP: 0.95, TopK: 20, MinP: 0),
        NonThinkingSampling = new(Temperature: 0.7, TopP: 0.8, TopK: 20, MinP: 0),
    };

    /// <summary>
    /// qwen2.5 系（qwen2.5:3b）。tools 対応・thinking 非対応。
    /// 推奨 temp0.7 / top_p0.8 / top_k20、repeat_penalty1.05。num_ctx はネイティブ上限の 32768。
    /// </summary>
    public static readonly ModelProfile Qwen25 = new()
    {
        Family = "qwen2.5",
        SupportsTools = true,
        SupportsThinking = false,
        NumCtx = 32768,
        Sampling = new(Temperature: 0.7, TopP: 0.8, TopK: 20, RepeatPenalty: 1.05),
    };

    /// <summary>qwen2.5-coder 系（qwen2.5-coder:3b）。qwen2.5 と同特性。</summary>
    public static readonly ModelProfile Qwen25Coder = Qwen25 with { Family = "qwen2.5-coder" };

    /// <summary>
    /// gemma3 系（gemma3:4b）。tools・thinking とも非対応（送るとエラー）。
    /// 推奨 temp1.0 / top_p0.95 / top_k64。ネイティブ上限 131072 だがメモリを抑え 32768 に留める。
    /// </summary>
    public static readonly ModelProfile Gemma3 = new()
    {
        Family = "gemma3",
        SupportsTools = false,
        SupportsThinking = false,
        NumCtx = 32768,
        Sampling = new(Temperature: 1.0, TopP: 0.95, TopK: 64),
    };

    /// <summary>
    /// 未知モデルの既定。tools は試行し（失敗時はクライアントがツール無しで再送）、
    /// thinking は安全側で無効、サンプリングはモデル既定に委ね、num_ctx のみ控えめに広げる。
    /// </summary>
    public static readonly ModelProfile Default = new()
    {
        Family = "default",
        SupportsTools = true,
        SupportsThinking = false,
        NumCtx = 8192,
        Sampling = SamplingOptions.Unspecified,
    };

    /// <summary>
    /// 実効コンテキスト窓（<c>num_ctx</c>）を返す。設定の上書き（&gt;0）を優先し、無ければモデル別の推奨値。
    /// Ollama への <c>num_ctx</c> と履歴トリムの上限を一致させるため、両者がこれを共有する。
    /// </summary>
    public static int EffectiveNumCtx(string? model, int numCtxOverride)
        => numCtxOverride > 0 ? numCtxOverride : Resolve(model).NumCtx;

    /// <summary>モデル名（"qwen3:4b" 等）からプロファイルを解決する。</summary>
    public static ModelProfile Resolve(string? model)
    {
        var id = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (id.StartsWith("qwen3")) return Qwen3;
        if (id.StartsWith("qwen2.5-coder")) return Qwen25Coder;
        if (id.StartsWith("qwen2.5")) return Qwen25;
        if (id.StartsWith("gemma3")) return Gemma3;
        return Default;
    }
}
