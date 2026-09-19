using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>git log --graph の1行。枝の継続行では Hash が null。</summary>
public sealed record GitLogRow(
    string Graph,
    string? Hash,
    string? ShortHash,
    string? Author,
    string? Date,
    string? Refs,
    string? Subject)
{
    public bool IsCommit => Hash is not null;

    /// <summary>親コミットのハッシュ（<c>%P</c>）。マージなら2つ以上。グラフのレーンはここから決まる
    /// （<see cref="GitCommitGraph"/>）。</summary>
    public IReadOnlyList<string> Parents { get; init; } = [];

    /// <summary>この行に付いた参照（ブランチ・タグ）を種類つきで。
    /// <b>毎回計算する</b>——record が合成する等値比較には宣言したフィールドも入るので、
    /// ここにキャッシュを持つと「まだ描画していない行」と「描画済みの行」が等しくなくなる。
    /// 一覧は <c>IndexOf</c>／<c>Contains</c> でその等値性に乗っているので、仮想化で何が
    /// 実体化されたか（＝スクロール位置）によって選択の追従が変わることになる。</summary>
    public IReadOnlyList<GitRefLabel> RefLabels => GitRefLabels.Parse(Refs);

    /// <summary>
    /// 行のツールチップ。先頭列はグラフ・refs・件名を1セルに詰めているので、列幅の内側に収まらない
    /// 限り<b>件名は途中で切れる</b>——列境界をドラッグして読むしかない状態を作らないための全文。
    /// refs があれば2行目に添える（これも同じセルで切れる側）。表示は短縮名にする
    /// （生の <c>%D</c> は <c>refs/remotes/origin/main</c> のような完全名で、読ませるものではない）。
    /// 枝の継続行は出すものが無いので null＝ツールチップ自体を出さない。
    /// </summary>
    public string? ToolTipText
    {
        get
        {
            if (Subject is not { Length: > 0 }) return null;
            if (RefLabels.Count == 0) return Subject;
            return $"{Subject}\n{string.Join(", ", RefLabels.Select(label => label.Name))}";
        }
    }
}

/// <summary>スタッシュ1件。</summary>
public sealed record GitStashEntry(string Ref, string Description);
