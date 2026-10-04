using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services;

/// <summary>一覧の1行。<see cref="IsAssigned"/> が false の行は、自分の Task の親として文脈のために出しているだけ
/// （自分に割り当たっているわけではない）。</summary>
public sealed record WorkItemTreeRow(AzureDevOpsWorkItem Item, int Depth, bool IsAssigned);

/// <summary>
/// 割り当たっている Work Item を親子でまとめて並べる（純ロジック）。User Story の下に、その Task が並ぶ形。
/// <para>親が自分に割り当たっていなくても、子が自分のものなら親を見出しとして出す——Task だけが
/// ばらばらに並んでも、どの Story の作業なのかが分からないから。</para>
/// <para>並びは更新の新しい順（<paramref name="assigned"/> の順）を保つ。まとまりの位置は、その中で
/// いちばん新しく更新された行の位置で決める。</para>
/// </summary>
public static class WorkItemTree
{
    public static IReadOnlyList<WorkItemTreeRow> Arrange(
        IReadOnlyList<AzureDevOpsWorkItem> assigned, IReadOnlyList<AzureDevOpsWorkItem> contextParents)
    {
        var assignedIds = assigned.Select(i => i.Id).ToHashSet();
        var all = new Dictionary<int, AzureDevOpsWorkItem>();
        foreach (var item in assigned) all.TryAdd(item.Id, item);
        foreach (var item in contextParents) all.TryAdd(item.Id, item);

        // 並び順の鍵：割り当て一覧での位置。文脈だけの親は子の位置を借りる（下で最小値を取る）。
        var order = new Dictionary<int, int>();
        for (var i = 0; i < assigned.Count; i++) order.TryAdd(assigned[i].Id, i);

        var children = new Dictionary<int, List<AzureDevOpsWorkItem>>();
        var roots = new List<AzureDevOpsWorkItem>();
        foreach (var item in all.Values)
        {
            // 親が一覧に居て、かつ自分自身ではないときだけぶら下げる（循環した壊れたリンクで迷子にしない）。
            if (item.ParentId != 0 && item.ParentId != item.Id && all.ContainsKey(item.ParentId)
                && !IsAncestor(all, item.Id, item.ParentId))
                (children.TryGetValue(item.ParentId, out var list) ? list : children[item.ParentId] = []).Add(item);
            else
                roots.Add(item);
        }

        var rank = new Dictionary<int, int>();
        int Rank(AzureDevOpsWorkItem item)
        {
            if (rank.TryGetValue(item.Id, out var cached)) return cached;
            var value = order.TryGetValue(item.Id, out var own) ? own : int.MaxValue;
            if (children.TryGetValue(item.Id, out var kids))
                foreach (var kid in kids)
                    value = System.Math.Min(value, Rank(kid));
            return rank[item.Id] = value;
        }

        var rows = new List<WorkItemTreeRow>();
        void Emit(AzureDevOpsWorkItem item, int depth)
        {
            rows.Add(new WorkItemTreeRow(item, depth, assignedIds.Contains(item.Id)));
            if (children.TryGetValue(item.Id, out var kids))
                foreach (var kid in kids.OrderBy(Rank).ThenBy(k => k.Id))
                    Emit(kid, depth + 1);
        }
        foreach (var root in roots.OrderBy(Rank).ThenBy(r => r.Id))
            Emit(root, 0);
        return rows;
    }

    /// <summary><paramref name="candidate"/> が <paramref name="id"/> の子孫か（親をたどって自分に戻るなら循環）。</summary>
    private static bool IsAncestor(Dictionary<int, AzureDevOpsWorkItem> all, int id, int candidate)
    {
        var seen = new HashSet<int>();
        var current = candidate;
        while (all.TryGetValue(current, out var node) && node.ParentId != 0 && seen.Add(current))
        {
            if (node.ParentId == id) return true;
            current = node.ParentId;
        }
        return false;
    }
}
