using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>行の帯のどこに線を引くか。行の高さを上半分・下半分に割って考える。</summary>
public enum GitGraphEdgeKind
{
    /// <summary>上端から丸まで（この行のコミットへ入ってくる線）。</summary>
    In,

    /// <summary>丸から下端まで（この行のコミットから親へ出ていく線）。</summary>
    Out,

    /// <summary>上端から下端まで素通り（この行とは関係の無い枝）。</summary>
    Through,
}

/// <summary>行の帯に引く線1本。<paramref name="FromLane"/> が上側、<paramref name="ToLane"/> が下側の列。</summary>
public readonly record struct GitGraphEdge(
    GitGraphEdgeKind Kind, int FromLane, int ToLane, int Color);

/// <summary>
/// 1行ぶんのグラフ。<paramref name="Lane"/> がこのコミットの丸の位置、
/// <paramref name="Edges"/> がこの行の帯に引く線。
/// </summary>
/// <param name="Lane">コミットの丸を置く列（0 始まり）。</param>
/// <param name="Color">丸の色番号。</param>
/// <param name="LaneCount">この行の帯で使われている列数（幅を決めるのに使う）。</param>
public sealed record GitGraphRow(
    int Lane, int Color, IReadOnlyList<GitGraphEdge> Edges, int LaneCount);

/// <summary>
/// コミットの親子関係から、描画用のレーン（縦の列）を組む純ロジック。
///
/// <para><c>git log --graph</c> の ASCII を読むのをやめて自分で組むのは、文字グラフが
/// <b>等幅の桁に縛られる</b>ため——行の高さも詰められないし、枝の色も付けられない。
/// 親ハッシュ（<c>%P</c>）さえあればレーンは決まる。</para>
///
/// <para>組み方は素朴な「開いているレーン」方式。各レーンは<b>次にそこへ来るはずのコミット</b>を
/// 覚えていて、行を処理するたびに
/// <list type="number">
/// <item>自分を待っているレーンを探す（無ければ空きレーンを取る＝新しい枝の先端）</item>
/// <item>そのレーンの待ち先を第1親に差し替える（＝枝が下へ続く）</item>
/// <item>第2親以降は、既にその親を待っているレーンがあればそこへ合流線を引き、
///   無ければ新しいレーンを開く（＝マージ）</item>
/// </list>
/// 残りのレーンは素通りの縦線になる。</para>
///
/// <para><b>順序は git に任せる</b>——<c>--topo-order</c> で来た並びをそのまま使う。ここで
/// 並べ替えると、一覧に出ている順番と食い違ってレーンが繋がらなくなる。親が一覧の外
/// （読み込んでいない古いコミット・浅いクローン）なら、その線は行の下端で途切れる。</para>
/// </summary>
public static class GitCommitGraph
{
    /// <summary>色は巡回させる。レーン番号ではなく<b>枝ごと</b>に割り当てる
    /// （レーン番号だと、枝が閉じて番号が詰まるたびに既存の線の色が変わる）。</summary>
    public const int ColorCount = 6;

    public static IReadOnlyList<GitGraphRow> Build(IReadOnlyList<GitLogRow> rows)
    {
        var result = new List<GitGraphRow>(rows.Count);
        if (rows.Count == 0) return result;

        // 各レーンが「次に待っているコミット」。null は空きレーン。
        var waiting = new List<string?>();
        var colors = new List<int>();
        var nextColor = 0;

        foreach (var row in rows)
        {
            if (row.Hash is not { Length: > 0 } hash)
            {
                // グラフを自分で組むので git の継続行はもう来ない。来ても場所だけ取らせる。
                result.Add(new GitGraphRow(0, 0, Array.Empty<GitGraphEdge>(), Math.Max(waiting.Count, 1)));
                continue;
            }

            // 「この行に入ってくる時点」の状態。以降 waiting を書き換えるので控えておく
            // ——素通りの判定は<b>入ってきた側</b>で決まる（この行で新しく開いたレーンは素通りではない）。
            var incoming = waiting.ToList();
            var lane = incoming.IndexOf(hash);
            if (lane < 0) lane = TakeFreeLane(waiting, colors, ref nextColor);
            var color = colors[lane];
            var edges = new List<GitGraphEdge>();

            for (var i = 0; i < incoming.Count; i++)
                if (incoming[i] == hash)
                    edges.Add(new GitGraphEdge(GitGraphEdgeKind.In, i, lane, colors[i]));

            // 自分を待っていた他のレーンは、ここで丸に合流したので閉じる。
            for (var i = 0; i < waiting.Count; i++)
                if (i != lane && waiting[i] == hash)
                    waiting[i] = null;

            var parents = row.Parents;
            // 第1親はこのレーンをそのまま下へ引き継ぐ（色も引き継ぐ＝枝の色が続く）。
            waiting[lane] = parents.Count > 0 ? parents[0] : null;
            if (parents.Count > 0)
                edges.Add(new GitGraphEdge(GitGraphEdgeKind.Out, lane, lane, color));

            // 第2親以降はマージ。既にその親を待っているレーンがあればそこへ、無ければ新しく開く。
            for (var p = 1; p < parents.Count; p++)
            {
                var parent = parents[p];
                var target = waiting.IndexOf(parent);
                if (target < 0)
                {
                    target = TakeFreeLane(waiting, colors, ref nextColor);
                    waiting[target] = parent;
                }
                edges.Add(new GitGraphEdge(GitGraphEdgeKind.Out, lane, target, colors[target]));
            }

            // 残り（この行と無関係なレーン）は縦に素通り。
            for (var i = 0; i < incoming.Count; i++)
                if (i != lane && incoming[i] is not null && incoming[i] != hash)
                    edges.Add(new GitGraphEdge(GitGraphEdgeKind.Through, i, i, colors[i]));

            var laneCount = Math.Max(Math.Max(incoming.Count, waiting.Count), lane + 1);
            TrimTrailingFreeLanes(waiting, colors);
            result.Add(new GitGraphRow(lane, color, edges, laneCount));
        }

        return result;
    }

    /// <summary>空きレーンを取る（無ければ足す）。新しい枝なので色も割り当て直す。</summary>
    private static int TakeFreeLane(List<string?> waiting, List<int> colors, ref int nextColor)
    {
        var lane = waiting.IndexOf(null);
        if (lane < 0)
        {
            waiting.Add(null);
            colors.Add(0);
            lane = waiting.Count - 1;
        }
        colors[lane] = nextColor;
        nextColor = (nextColor + 1) % ColorCount;
        return lane;
    }

    /// <summary>右端の空きレーンは畳む（枝が閉じたあとも幅を取り続けないように）。</summary>
    private static void TrimTrailingFreeLanes(List<string?> waiting, List<int> colors)
    {
        while (waiting.Count > 0 && waiting[^1] is null)
        {
            waiting.RemoveAt(waiting.Count - 1);
            colors.RemoveAt(colors.Count - 1);
        }
    }
}
