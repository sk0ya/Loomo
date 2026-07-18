using System.Text.Json.Nodes;

namespace sk0ya.Loomo.Ai.Clients;

/// <summary>
/// モデルが使うチャットテンプレート／ツール呼び出しの記法。プロンプト組み立てと
/// 本文からのツール呼び出し抽出を、これに従って切り替える。
/// </summary>
public enum ChatFormat
{
    /// <summary>Phi-4 系：<c>&lt;|system|&gt;…&lt;|tool|&gt;[…]&lt;|/tool|&gt;&lt;|end|&gt;</c>、
    /// ツール呼び出しは本文に JSON 配列 <c>[{"name":…,"arguments":{…}}]</c>。</summary>
    Phi4,

    /// <summary>Qwen3 系（ChatML）：<c>&lt;|im_start|&gt;role…&lt;|im_end|&gt;</c>、ツール定義は system に
    /// <c>&lt;tools&gt;…&lt;/tools&gt;</c>、呼び出しは Hermes 風 <c>&lt;tool_call&gt;{…}&lt;/tool_call&gt;</c>。</summary>
    Qwen3,
}

/// <summary>
/// モデル別のチャット形式、サンプリング、コンテキスト長を保持する。
/// </summary>
public sealed record ModelProfile
{
    /// <summary>プロファイル名（診断・テスト用。例: "qwen3"）。</summary>
    public required string Family { get; init; }

    /// <summary>このモデルのチャットテンプレート／ツール記法。プロンプト組み立てと本文パースの切り替えに使う。</summary>
    public ChatFormat Format { get; init; } = ChatFormat.Phi4;

    /// <summary>
    /// <c>num_ctx</c> に設定する推奨コンテキスト長。Ollama 既定の 4096 はエージェント用途
    /// （ファイル読み取りやツール結果でコンテキストが伸びる）には狭すぎるため、各モデルの
    /// ネイティブ上限内で広めに取る。0以下なら未設定（Ollama 既定に委ねる）。
    /// </summary>
    public int NumCtx { get; init; }

    /// <summary>サンプリング既定（thinking 有効時、または thinking を使わない通常時）。</summary>
    public SamplingOptions Sampling { get; init; } = SamplingOptions.Unspecified;

    /// <summary>
    /// 1応答の生成上限。0 以下ならユーザー設定の MaxTokens をそのまま使う。
    /// 小型ローカルモデルでは過大な num_predict が tool call 失敗時の待ち時間を増やすため、
    /// モデル別に実用上限を持てるようにする。
    /// </summary>
    public int MaxOutputTokens { get; init; }

}

/// <summary>
/// ローカル推論エンジンへ渡すサンプリングパラメータ。
/// null の項目は指定せず、モデルの既定に委ねる。
/// </summary>
public sealed record SamplingOptions(
    double? Temperature = null,
    double? TopP = null,
    int? TopK = null,
    double? MinP = null,
    double? RepeatPenalty = null)
{
    /// <summary>何も上書きしない（モデル既定に全面的に委ねる）。</summary>
    public static readonly SamplingOptions Unspecified = new();

    /// <summary>指定された項目だけを <paramref name="options"/> に書き込む。</summary>
    public void ApplyTo(JsonObject options)
    {
        if (Temperature is { } t) options["temperature"] = t;
        if (TopP is { } p) options["top_p"] = p;
        if (TopK is { } k) options["top_k"] = k;
        if (MinP is { } m) options["min_p"] = m;
        if (RepeatPenalty is { } r) options["repeat_penalty"] = r;
    }
}
