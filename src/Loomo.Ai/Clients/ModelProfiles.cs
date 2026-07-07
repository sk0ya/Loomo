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
    /// JSON とツール名の安定性が高かった。短周期ループの停止は OnnxGenAiEngine のデコードループ反復ガードで担保する。
    ///
    /// 性能最適化:
    /// コンテキスト窓（max_length のもと）は 8192 に抑える。tool calling 用途では、巨大な履歴より
    /// 小さいコンテキストと短い応答を優先した方が初回応答とツール選択が安定して速い。
    /// </summary>
    public static readonly ModelProfile Phi4Mini = new()
    {
        Family = "phi4-mini",
        NumCtx = 8192,
        // write_file/edit_file は content 全文を1応答のJSON文字列として出す。1024だと数百字を超える
        // ファイルで生成が途中打ち切りになり、閉じ引用符/波括弧を欠いた壊れたJSON→ツール呼び出し解釈失敗
        // →エラーになっていた（実障害）。4096に上げて実用的なファイルサイズを賄う。
        MaxOutputTokens = 4096,
        Sampling = SamplingOptions.Unspecified,
    };

    /// <summary>
    /// Qwen3 系（既定の 4B GGUF Q4_K_M、および lokinfey の CPU int4 ビルド）。ChatML テンプレート＋Hermes 風 tool call を使う。
    ///
    /// サンプリング:
    /// Qwen3 公式は<b>greedy デコードを明確に非推奨</b>（繰り返し崩壊・性能低下を招く）としており、
    /// non-thinking モードの推奨値 temperature=0.7 / top_p=0.8 / top_k=20 を用いる。
    /// さらに ORT CPU で有効な repetition_penalty を軽く効かせて長文反復を抑える
    /// （phi4-mini は greedy が安定だったが、Qwen3 は別系統なので公式推奨に従う）。
    ///
    /// 性能最適化:
    /// ネイティブ上限は 32768 だが、tool calling 用途では小さいコンテキストの方が初回応答と
    /// ツール選択が安定して速いため、phi4-mini と同じ 8192 に抑える。
    /// </summary>
    public static readonly ModelProfile Qwen3 = new()
    {
        Family = "qwen3",
        Format = ChatFormat.Qwen3,
        NumCtx = 8192,
        // Phi4Mini と同じ理由（上のコメント参照）で 1024 は write_file の長文content途中打ち切りを招くため 4096 に上げる。
        MaxOutputTokens = 4096,
        Sampling = new SamplingOptions(Temperature: 0.7, TopP: 0.8, TopK: 20, RepeatPenalty: 1.05),
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
        // "qwen3" / "qwen-3" / "qwen_3" いずれの表記でも拾う（DLフォルダ名 "qwen3-4b-q4_k_m" もここに合致）。
        if (id.Contains("qwen3") || id.Contains("qwen-3") || id.Contains("qwen_3")) return Qwen3;
        return Default;
    }
}
