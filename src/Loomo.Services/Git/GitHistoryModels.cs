using System;
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

    /// <summary>親コミットのハッシュ（<c>%P</c>）を git が書いたまま＝空白区切りの1本の文字列で持つ。
    /// <b>配列で持たない</b>——record が合成する等値比較には宣言したフィールドも入り、配列は参照比較
    /// なので、同じ出力を読み直しただけの行同士が等しくなくなる（一覧の <c>IndexOf</c>／
    /// <c>Contains</c> がその等値性に乗っている）。</summary>
    public string ParentsText { get; init; } = "";

    /// <summary>親コミットのハッシュ。マージなら2つ以上。グラフのレーンはここから決まる
    /// （<see cref="GitCommitGraph"/>）。</summary>
    public IReadOnlyList<string> Parents =>
        ParentsText.Length == 0 ? [] : ParentsText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

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
public sealed record GitStashEntry(string Ref, string Description)
{
    /// <summary>パッチとして保存するときの既定のファイル名（<c>stash-0-On-main-作業中.patch</c>）。
    /// 番号は <c>stash@{n}</c> の n、続けて説明をファイル名に使えない文字を落として詰めたもの。</summary>
    public string PatchFileName
    {
        get
        {
            var open = Ref.IndexOf('{');
            var close = Ref.IndexOf('}');
            var index = open >= 0 && close > open ? Ref[(open + 1)..close] : "";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var words = Description
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => new string(word.Where(c => !invalid.Contains(c)).ToArray()))
                .Where(word => word.Length > 0);
            var label = string.Join('-', words);
            if (label.Length > MaxPatchLabel) label = label[..MaxPatchLabel].TrimEnd('-', '.');
            var name = string.Join('-', new[] { "stash", index, label }.Where(part => part.Length > 0));
            return name + ".patch";
        }
    }

    private const int MaxPatchLabel = 40;
}

/// <summary>
/// スタッシュの中身を差分で見るための解決結果。スタッシュは「作業ツリーの状態」を表すコミットで、
/// 1 つ目の親（<paramref name="BaseHash"/>）が退避したときの HEAD。親→スタッシュの範囲が退避した変更になる。
/// 未追跡のファイル（<c>push -u</c>）は 3 つ目の親の別のツリーに入っていて、この範囲には出ない——
/// その件数を <paramref name="UntrackedCount"/> に持つ（パッチの書き出しには含まれる）。
/// </summary>
public sealed record GitStashSnapshot(string BaseHash, string StashHash, int UntrackedCount);
