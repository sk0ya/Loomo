using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 「この差分を見せてほしい」という要求そのもの（<b>何を</b>だけを持ち、<b>どこへ出すか</b>は持たない）。
///
/// <para>出し先（Diff ペイン／別ウィンドウ）を決めるのはホスト（<c>ShellWindow.ShowDiff</c>）の仕事で、
/// 素材を送る側（Git・エディタ・エクスプローラー）はペインの VM を直接書き換えない——書き換えてから
/// 「ペインを出して」と頼む作りだと、ペインが隠れているときの逃げ道（別ウィンドウ）が選べず、
/// 頼む側の数だけ同じ判断が散らばるため（設計書 §23.3 の「渡す側が見せ方を決める」の続きで、
/// 渡す側が決めるのは素材まで）。</para>
///
/// <para>受け手は <see cref="DiffSessionViewModel.ShowAsync"/> ひとつ。ペインの VM でも切り離し
/// ウィンドウの VM でも<b>同じ要求が同じように開く</b>ことが要点で、そうでないと「隠れていたら
/// 別ウィンドウ」が別機能になってしまう。</para>
/// </summary>
public abstract record DiffOpenTarget
{
    private DiffOpenTarget() { }   // 派生はこの中の4つだけ（受け手の振り分けを閉じた集合にする）

    /// <summary>切り離しウィンドウのタブ名。<b>短く</b>——タブは何枚も並ぶので、長いと隣が潰れて
    /// どれがどれだか分からなくなる。ハッシュは7桁、飾りの語（「コミット」「作業ツリー」）は落とし、
    /// それでも長ければ末尾を詰める。</summary>
    public string WindowTitle => TitleFor(IconPath);

    /// <summary>窓の中で見ているファイルが変わったときのタブ名（「次の差分」でファイルを跨いだとき）。
    /// 種別ごとの短い添え字（@ハッシュ 等）は保つ——どの差分の中に居るのかが消えないように。</summary>
    public abstract string TitleFor(string? path);

    /// <summary>この差分の出どころのファイル（タブのアイコンに使う。無ければ空）。</summary>
    public virtual string IconPath => "";

    /// <summary>Git のコミット範囲（1コミットなら <paramref name="FromHash"/> は null）。</summary>
    public sealed record CommitRange(string? FromHash, string ToHash, string Label) : DiffOpenTarget
    {
        public override string TitleFor(string? path) => Trim(WithFile(path, Range));

        private string Range => FromHash is null
            ? $"@{Short(ToHash)}" : $"@{Short(FromHash)}→{Short(ToHash)}";
    }

    /// <summary>1コミットの1ファイル。<paramref name="LineInCommit"/> はコミット時点の新側行番号
    /// （1始まり。0なら通常の「最初の変更へ」）。</summary>
    public sealed record CommitFile(string Hash, string Label, string? Path, int LineInCommit)
        : DiffOpenTarget
    {
        public override string TitleFor(string? path) => Trim(WithFile(path, $"@{Short(Hash)}"));
        public override string IconPath => Path ?? "";
    }

    /// <summary>作業ツリーの1ファイル（ステージ済み／未ステージ）。</summary>
    public sealed record WorkingTreeFile(GitChangeEntry Entry, bool IsStaged) : DiffOpenTarget
    {
        // 作業ツリーは部屋の既定の文脈なので、名乗らずファイル名だけでよい。
        public override string TitleFor(string? path) => Trim(FileName(path) is { Length: > 0 } name
            ? name : FileName(Entry.Path));
        public override string IconPath => Entry.Path;
    }

    /// <summary>アドホック比較（クリップボード ↔ 選択範囲 など。Git には紐づかない）。</summary>
    public sealed record Comparison(DiffComparison Value) : DiffOpenTarget
    {
        // 「A ↔ B」は両側とも説明的で長くなりがちなので、タブでは左右をそれぞれ詰めて並べる。
        public override string TitleFor(string? path)
            => $"{Trim(Value.LeftTitle, Half)} ↔ {Trim(Value.RightTitle, Half)}";
        public override string IconPath => Value.FilePath;
    }

    /// <summary>タブ名の上限。日本語混じりでも一目で読める程度に詰める。</summary>
    private const int MaxTitle = 28;
    /// <summary>「A ↔ B」の片側ぶん（区切りを入れて全体が <see cref="MaxTitle"/> に収まる長さ）。</summary>
    private const int Half = (MaxTitle - 3) / 2;

    /// <summary>ファイル名＋添え字。ファイルが分からないときは添え字だけ（先頭の <c>@</c> は残す）。</summary>
    private static string WithFile(string? path, string suffix)
        => FileName(path) is { Length: > 0 } name ? name + suffix : suffix;

    private static string FileName(string? path)
        => string.IsNullOrWhiteSpace(path) ? "" : System.IO.Path.GetFileName(path);

    /// <summary>コミットハッシュは7桁に詰める（タブで全長は読めず、7桁で十分に見分けが付く）。</summary>
    internal static string Short(string? hash)
        => hash is { Length: > 7 } long_ ? long_[..7] : hash ?? "";

    /// <summary>長すぎる見出しの末尾を詰める（頭は残す——先頭にファイル名が来るため）。</summary>
    private static string Trim(string text, int max = MaxTitle)
        => text.Length <= max ? text : text[..(max - 1)] + "…";
}
