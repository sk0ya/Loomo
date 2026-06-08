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
/// モデル別の最適な呼び出しプロファイル。capabilities（tools / thinking 対応）と、
/// 各モデルファミリの公式推奨サンプリング・コンテキスト長を保持し、Ollama
/// <c>/api/chat</c> の <c>options</c> 構築や <c>think</c>・<c>tools</c> の送出可否に使う。
/// 値は各モデルの公式推奨と <c>ollama show</c> の capabilities を根拠にしている。
/// </summary>
public sealed record ModelProfile
{
    /// <summary>プロファイル名（診断・テスト用。例: "qwen3"）。</summary>
    public required string Family { get; init; }

    /// <summary>このモデルのチャットテンプレート／ツール記法。プロンプト組み立てと本文パースの切り替えに使う。</summary>
    public ChatFormat Format { get; init; } = ChatFormat.Phi4;

    /// <summary>function calling（tools）に対応するか。false なら tools を送らない。</summary>
    public bool SupportsTools { get; init; } = true;

    /// <summary>thinking（<c>think</c> 真偽値）に対応するか。false なら think を一切送らない
    /// （非対応モデルは <c>think:true</c> で「does not support thinking」エラーになる）。</summary>
    public bool SupportsThinking { get; init; }

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

    /// <summary>
    /// thinking を無効化して動かすときのサンプリング上書き（qwen3 等は thinking 有無で
    /// 推奨温度が変わる）。null なら <see cref="Sampling"/> をそのまま使う。
    /// </summary>
    public SamplingOptions? NonThinkingSampling { get; init; }

    /// <summary>現在の thinking 状態に応じたサンプリング設定を返す。</summary>
    public SamplingOptions SamplingFor(bool thinking)
        => thinking || NonThinkingSampling is null ? Sampling : NonThinkingSampling;
}

/// <summary>
/// Ollama <c>options</c> に展開するサンプリングパラメータ。
/// null の項目は送らず、モデル（Modelfile）の既定に委ねる。
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
