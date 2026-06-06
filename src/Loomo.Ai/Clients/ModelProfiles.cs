namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// モデル名から <see cref="ModelProfile"/> を解決する。ファミリ名（タグ ":xxx" を含む接頭辞）で判定し、
/// 現在インストールされている各モデルに合わせて tools / thinking / サンプリングを最適化する。
/// 既知でないモデルは安全側の既定（tools は試行、thinking なし、控えめな num_ctx）にフォールバックする。
/// </summary>
public static class ModelProfiles
{
    /// <summary>
    /// phi4-mini 系（microsoft/Phi-4-mini-instruct-onnx）。CPU で in-process 駆動する想定。
    /// Phi-4 推奨のサンプリングを ORT-GenAI の search option（temperature / top_p / repetition_penalty）へ写す。
    /// 小型モデルで繰り返しが出やすいため repetition_penalty 1.05 を添える。
    ///
    /// 性能最適化:
    /// コンテキスト窓（max_length のもと）は 8192 に抑える。tool calling 用途では、巨大な履歴より
    /// 小さいコンテキストと短い応答を優先した方が初回応答とツール選択が安定して速い。
    /// </summary>
    public static readonly ModelProfile Phi4Mini = new()
    {
        Family = "phi4-mini",
        NumCtx = 8192,
        MaxOutputTokens = 1024,
        Sampling = new(Temperature: 0.2, TopP: 0.9, RepeatPenalty: 1.05),
    };

    /// <summary>未知モデルの既定。サンプリングはモデル既定に委ね、コンテキストのみ控えめに広げる。</summary>
    public static readonly ModelProfile Default = new()
    {
        Family = "default",
        NumCtx = 8192,
        Sampling = SamplingOptions.Unspecified,
    };

    /// <summary>
    /// 実効コンテキスト窓（<c>num_ctx</c>）を返す。設定の上書き（&gt;0）を優先し、無ければモデル別の推奨値。
    /// Ollama への <c>num_ctx</c> と履歴トリムの上限を一致させるため、両者がこれを共有する。
    /// </summary>
    public static int EffectiveNumCtx(string? model, int numCtxOverride)
        => numCtxOverride > 0 ? numCtxOverride : Resolve(model).NumCtx;

    /// <summary>モデル名／フォルダ名からプロファイルを解決する。
    /// phi4-mini はハイフン有無（"phi4-mini" / "phi-4-mini"）どちらの表記でも拾う
    /// （ローカルフォルダ名 "phi-4-mini-instruct-cpu-int4" もここに合致させる）。</summary>
    public static ModelProfile Resolve(string? model)
    {
        var id = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (id.Contains("phi4-mini") || id.Contains("phi-4-mini")) return Phi4Mini;
        return Default;
    }
}
