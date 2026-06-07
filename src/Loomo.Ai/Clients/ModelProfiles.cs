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
    /// ツール呼び出し用途では sampling を指定しない（greedy 相当）。実測では temperature/top_p を入れると
    /// <c>rg</c>/<c>read_file</c>/<c>build</c> 等の架空ツール名や長い後続説明が増え、greedy 相当の方が
    /// JSON とツール名の安定性が高かった。短周期ループの停止は Phi4Engine のデコードループ反復ガードで担保する。
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
        Sampling = SamplingOptions.Unspecified,
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
