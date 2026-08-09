using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.ViewModels;

public sealed record DiffRowVm(string Kind, string Text);

public sealed record DiffHunkVm(int Index, string HeaderLine, string Summary, bool IsStaged)
{
    public string ActionLabel => IsStaged ? "アンステージ" : "ステージ";
}

public sealed record DiffSideRowVm(
    string LeftKind, string LeftText, string RightKind, string RightText,
    string LeftLine, string RightLine);

/// <summary>Diff ペインが今どこから差分を取っているか。</summary>
public enum DiffSource
{
    /// <summary>Git（作業ツリー／コミット範囲）。</summary>
    Git,
    /// <summary>AI 変更（ファイル変更ジャーナル）。</summary>
    Ai,
    /// <summary>アドホック比較（<see cref="DiffComparison"/>）。ファイルにも Git にも依らない素材2つ。</summary>
    Compare,
}

/// <summary>
/// アドホック比較の素材：左＝旧、右＝新の任意テキスト2つ。エディタの選択・ターミナルの選択・
/// クリップボード・ディスク上のファイルなど、部屋の中の素材ならどれでも左右に置ける（設計書 §23.3）。
/// <paramref name="FilePath"/> は「この比較の出どころのファイル」（あれば）。エディタで開く・行へ飛ぶの宛先に使う。
/// <paramref name="FileIsLeft"/> は、そのファイルの中身が**左右どちらの側**かを表す（既定は右＝新側。
/// git 差分と同じ向き）。「ファイル ↔ クリップボード」のようにファイルが左のときは true にする。
/// これを取り違えると「この行をエディタで開く」が反対側の行番号へ飛ぶ。
/// </summary>
public sealed record DiffComparison(
    string LeftTitle, string LeftText, string RightTitle, string RightText, string FilePath = "",
    bool FileIsLeft = false)
{
    public DiffComparison Swapped()
        => new(RightTitle, RightText, LeftTitle, LeftText, FilePath, !FileIsLeft);

    /// <summary>右側だけ別の素材へ差し替える。右がそのファイルだった比較（<see cref="FileIsLeft"/> が false）では
    /// 出どころのファイルも一緒に手放す——残すと「右はもう別物なのに、行はそのファイルの行番号として開く」
    /// 取り違えになるため。</summary>
    public DiffComparison WithRight(string title, string text)
        => this with { RightTitle = title, RightText = text, FilePath = FileIsLeft ? FilePath : "" };

    /// <summary>ファイル一覧に出す見出し（どちらとどちらを比べているか）。</summary>
    public string DisplayPath => $"{LeftTitle} ↔ {RightTitle}";

    /// <summary>差分本体の上に出す帯（どちらが左でどちらが右か）。</summary>
    public string Caption => $"◀ 左: {LeftTitle}　　右: {RightTitle} ▶";
}

public sealed class DiffFileItem
{
    public required string FullPath { get; init; }
    public required string DisplayPath { get; init; }
    public required string Badge { get; init; }
    public string Stats { get; init; } = "";
    public string FileName => Path.GetFileName(FullPath);
    public bool IsAi { get; init; }
    public bool IsNew { get; init; }
    /// <summary>アドホック比較の項目なら、その素材そのもの（ストックに入っている実体を指す）。
    /// 一覧は更新のたびに作り直されるので、「どの比較か」は表示名やパスではなくこの参照で追う
    /// ——比較はパスを持たないことがあり、同じファイルの比較を複数ストックすることもあるため。</summary>
    public DiffComparison? Comparison { get; init; }
    /// <summary>アドホック比較（<see cref="DiffComparison"/>）の項目。Git にも変更ジャーナルにも紐づかない。</summary>
    public bool IsCompare => Comparison is not null;
    /// <summary><see cref="FullPath"/> のファイルの中身が左側にあるか（既定は右＝新側。git 差分と同じ向き）。
    /// 差分の行から実ファイルの行番号を引くとき、どちら側の行番号を読むかを決める。</summary>
    public bool FileIsLeft { get; init; }
    public string? OldContent { get; init; }
    public string? NewContent { get; init; }
    /// <summary>差分を git ではなく項目が持つ全文2つから組み立てるか（AI変更・アドホック比較がこれ）。</summary>
    public bool UsesInlineContent => IsAi || IsCompare;
    public bool CanRevert => IsAi && (IsNew || OldContent is not null);
    public GitChangeEntry? Entry { get; init; }
    /// <summary>作業ツリーの変更として破棄できる項目か（Git の作業ツリー項目だけが持つ）。</summary>
    public bool CanDiscard => Entry is not null;
    public bool IsStaged { get; init; }
    public GitCommitFileChange? CommitFile { get; init; }
}
