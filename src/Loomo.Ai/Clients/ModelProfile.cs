using System.Text.Json.Nodes;

namespace sk0ya.Loomo.Ai.Clients;

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
    /// thinking を無効化して動かすときのサンプリング上書き（qwen3 等は thinking 有無で
    /// 推奨温度が変わる）。null なら <see cref="Sampling"/> をそのまま使う。
    /// </summary>
    public SamplingOptions? NonThinkingSampling { get; init; }

    /// <summary>
    /// システムプロンプト末尾へ動的に添えるモデル固有のスタイル指示（任意）。
    /// 冗長になりやすいモデル（phi4-mini 等）へ簡潔さを促す等に使う。
    /// 空文字なら何も添えない。ユーザー設定のシステムプロンプトは書き換えない。
    /// この指示は会話を通じて不変なので、システムプロンプトの安定したプレフィックスに含めてよい
    /// （Ollama はプレフィックスの KV キャッシュを再利用するため、毎ターン変わる内容は別に置く）。
    /// </summary>
    public string StyleGuidance { get; init; } = string.Empty;

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
