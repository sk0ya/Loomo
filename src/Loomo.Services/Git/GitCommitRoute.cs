using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>経路の1歩（上＝新しい側から順に並ぶ）。</summary>
/// <param name="Hash">コミット。</param>
/// <param name="IsMerge">このコミットが<b>第2親以降として</b>経路を取り込んだマージか
/// ——「どのマージで入ってきたか」の答えになる行。</param>
public sealed record GitRouteStep(string Hash, bool IsMerge);

/// <summary>
/// コミット1件の「経路」。グラフでコミットを押したときに強調する線の集合。
///
/// <para>経路は2つの向きを繋いだもの：
/// <list type="bullet">
/// <item><b>上り</b>——そのコミットが一覧の先端（HEAD があれば HEAD、無ければいずれかの先端）まで
///   どの枝・どのマージを通って辿り着くか。「この変更はどのマージで main に入ったか」の答え。</item>
/// <item><b>下り</b>——そのコミットの第1親を辿った系譜（＝その枝の歩み）。</item>
/// </list></para>
///
/// <para>上りは<b>マージを跨ぐ回数が最少</b>の道を選ぶ（第1親として続く辺は0、第2親以降として
/// 取り込まれる辺は1の重み）。一番素直な「枝の上を進み、合流したら合流先の幹を進む」がこれになる。
/// 同じ重みなら短い方。</para>
///
/// <para>線の引き当ては<b>親子の組</b>で行う（<see cref="Contains(GitGraphEdge)"/>）。レーン番号や色では
/// 判定しない——同じレーンが時間をおいて別の枝に使い回されるので、番号だと無関係な線まで光る。</para>
/// </summary>
public sealed class GitCommitRoute
{
    private readonly HashSet<string> _nodes;
    private readonly HashSet<(string Child, string Parent)> _links;

    private GitCommitRoute(
        string focus, string? tip, IReadOnlyList<GitRouteStep> upward,
        HashSet<string> nodes, HashSet<(string, string)> links)
    {
        Focus = focus;
        Tip = tip;
        Upward = upward;
        _nodes = nodes;
        _links = links;
    }

    /// <summary>押されたコミット。</summary>
    public string Focus { get; }

    /// <summary>上りの行き着いた先端。自分自身が先端なら <see cref="Focus"/>。</summary>
    public string? Tip { get; }

    /// <summary>上りの道のり。先端から <see cref="Focus"/> の手前まで、上（新しい側）から順に。</summary>
    public IReadOnlyList<GitRouteStep> Upward { get; }

    /// <summary>上りで跨いだマージ（＝このコミットを取り込んだマージ）。上から順に。</summary>
    public IEnumerable<GitRouteStep> Merges => Upward.Where(step => step.IsMerge);

    public bool Contains(string? hash) => hash is not null && _nodes.Contains(hash);

    /// <summary>この線が経路の一部か。線に相乗りしている子のどれか1つでも経路の親子の組なら光らせる。</summary>
    public bool Contains(GitGraphEdge edge)
    {
        if (edge.Parent is not { } parent || edge.Children is not { } children) return false;
        foreach (var child in children)
            if (_links.Contains((child, parent)))
                return true;
        return false;
    }

    /// <summary><paramref name="focus"/> の経路を組む。一覧に居なければ null。</summary>
    public static GitCommitRoute? Build(IReadOnlyList<GitLogRow> rows, string focus)
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        var parentsOf = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        string? head = null;
        foreach (var row in rows)
        {
            if (row.Hash is not { Length: > 0 } hash || order.ContainsKey(hash)) continue;
            order[hash] = order.Count;
            parentsOf[hash] = row.Parents;
            if (head is null && row.RefLabels.Any(label => label.IsHead)) head = hash;
        }
        if (!order.ContainsKey(focus)) return null;

        // 子 → (子, 自分が第1親か)。一覧に出てこない親は経路に乗らないので繋がない。
        var childrenOf = new Dictionary<string, List<(string Child, bool First)>>(StringComparer.Ordinal);
        foreach (var (child, parents) in parentsOf)
            for (var p = 0; p < parents.Count; p++)
            {
                if (!order.ContainsKey(parents[p])) continue;
                if (!childrenOf.TryGetValue(parents[p], out var list))
                    childrenOf[parents[p]] = list = new();
                list.Add((child, p == 0));
            }

        var (tip, upward) = Climb(focus, order, childrenOf, head);

        var nodes = new HashSet<string>(StringComparer.Ordinal) { focus };
        var links = new HashSet<(string, string)>();
        var below = focus;
        for (var i = upward.Count - 1; i >= 0; i--)
        {
            nodes.Add(upward[i].Hash);
            links.Add((upward[i].Hash, below));
            below = upward[i].Hash;
        }

        // 下り：第1親の系譜。一覧の外へ出たらそこで止める。
        var current = focus;
        while (parentsOf.TryGetValue(current, out var parents) && parents.Count > 0
               && order.ContainsKey(parents[0]) && nodes.Add(parents[0]))
        {
            links.Add((current, parents[0]));
            current = parents[0];
        }

        return new GitCommitRoute(focus, tip, upward, nodes, links);
    }

    /// <summary>
    /// 上りの道を探す（0-1 BFS）。HEAD に届くなら HEAD へ、届かなければ重み最少の先端
    /// （子を持たないコミット）のうち一覧で一番上のものへ。
    /// </summary>
    private static (string Tip, IReadOnlyList<GitRouteStep> Upward) Climb(
        string focus,
        Dictionary<string, int> order,
        Dictionary<string, List<(string Child, bool First)>> childrenOf,
        string? head)
    {
        var cost = new Dictionary<string, int>(StringComparer.Ordinal) { [focus] = 0 };
        var hops = new Dictionary<string, int>(StringComparer.Ordinal) { [focus] = 0 };
        // 自分 → (一つ下＝来た側, そこから第2親以降として取り込んだか)
        var came = new Dictionary<string, (string From, bool Merge)>(StringComparer.Ordinal);
        var queue = new LinkedList<string>();
        queue.AddFirst(focus);
        while (queue.First is { } first)
        {
            var current = first.Value;
            queue.RemoveFirst();
            if (!childrenOf.TryGetValue(current, out var children)) continue;
            foreach (var (child, isFirst) in children)
            {
                var c = cost[current] + (isFirst ? 0 : 1);
                var h = hops[current] + 1;
                if (cost.TryGetValue(child, out var known)
                    && (known < c || (known == c && hops[child] <= h)))
                    continue;
                cost[child] = c;
                hops[child] = h;
                came[child] = (current, !isFirst);
                if (isFirst) queue.AddFirst(child);
                else queue.AddLast(child);
            }
        }

        string tip;
        if (head is not null && cost.ContainsKey(head))
        {
            tip = head;
        }
        else
        {
            tip = cost.Keys
                .Where(hash => !childrenOf.ContainsKey(hash))
                .OrderBy(hash => cost[hash])
                .ThenBy(hash => order[hash])
                .First();
        }

        var steps = new List<GitRouteStep>();
        for (var at = tip; at != focus; at = came[at].From)
            steps.Add(new GitRouteStep(at, came[at].Merge));
        return (tip, steps);
    }
}
