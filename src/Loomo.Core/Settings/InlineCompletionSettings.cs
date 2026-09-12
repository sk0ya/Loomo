namespace sk0ya.Loomo.Core.Settings;

/// <summary>
/// 入力の先読み（キャレットの先に薄く出る提案）のうち、ローカル LLM が作る方の設定。
/// エディタ内蔵の予測（既出行からの補完）は言語サーバーもモデルも要らないので常に動く——
/// ここで有効にするのは、その上に重ねる<b>FIM（fill-in-the-middle）モデルによる行補完</b>。
///
/// <para>既定は無効。専用モデル（数百 MB）のダウンロードが要るため、勝手に始めない。</para>
/// </summary>
public sealed class InlineCompletionSettings
{
    /// <summary>ローカル LLM による先読みを使うか。<see cref="ModelPath"/> が無ければ有効でも動かない。</summary>
    public bool Enabled { get; set; }

    /// <summary>FIM モデル（<c>.gguf</c>）のパス。チャット用モデルとは別に常駐する。</summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// キャレットより前に見せる行数。
    ///
    /// <para>この部分は打鍵しても頭から変わらないので、KV キャッシュがそのまま効く
    /// ——長くしても打鍵ごとの費用はほとんど増えない（初回だけ高くなる）。</para>
    /// </summary>
    public int PrefixLines { get; set; } = 30;

    /// <summary>
    /// キャレットより後ろに見せる行数。
    ///
    /// <para><b>ここは短くする。</b>FIM のプロンプトでは後ろの本文がキャレット位置より<i>後</i>に並ぶため、
    /// 打鍵のたびに丸ごと送り直しになる。実測（Ryzen 5 3500・0.5B・Q8_0）で 10 行なら約 520ms、
    /// 3 行なら約 280ms と、体感を決めているのがこの値だった。</para>
    /// </summary>
    public int SuffixLines { get; set; } = 3;

    /// <summary>
    /// 1 提案あたりの最大生成トークン数。1 行しか表示しないので長く作っても捨てるだけで、
    /// 生成の時間はそのまま<b>手が止まってから出るまでの待ち</b>になる。
    /// </summary>
    public int MaxTokens { get; set; } = 8;

    /// <summary>
    /// 生成（decode）に使うスレッド数。0 以下なら 2。
    ///
    /// <para>実測（Ryzen 5 3500・6 コア）で decode は<b>2 スレッドで頭打ち</b>——
    /// 1→325ms、2→174ms、3→165ms、6→166ms。1 提案のうち時間が長いのは decode 側なので、
    /// ここを 2 に抑えるのが CPU の取り分を減らす一番効く手になる。</para>
    /// </summary>
    public int Threads { get; set; }

    /// <summary>
    /// 前処理（prefill）に使うスレッド数。0 以下なら「コア数 − 2」（最低 1）。
    ///
    /// <para>decode と違い prefill は素直に並列化が効く（1→648ms、3→236ms、6→158ms）。
    /// ただし短いバーストなので、UI と言語サーバーのぶんを残した上で多めに割り当てる。
    /// decode 2 ／ prefill 4 の組で打鍵あたり 357ms、全コア（6/6）の 306ms に対して
    /// 17% 遅いだけで、長い方の decode 中は 2 コアしか使わない。</para>
    /// </summary>
    public int PrefillThreads { get; set; }
}
