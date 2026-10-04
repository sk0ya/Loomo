using System;
using System.Collections.Generic;
using System.Linq;

namespace sk0ya.Loomo.Services;

public enum WorkItemListEntryKind
{
    WorkItem,
    PullRequest,
    /// <summary>見出し（「Work Item に紐づかない PR」）。</summary>
    Section,
}

/// <summary>一覧の1行。<see cref="ParentIndex"/> は同じ一覧での親の位置（無ければ -1）——フィルターで
/// 子が残ったときに親を道しるべとして残すのに使う。</summary>
public sealed record WorkItemListEntry(
    WorkItemListEntryKind Kind,
    AzureDevOpsWorkItem? WorkItem,
    AzureDevOpsPullRequest? PullRequest,
    string SectionTitle,
    int Depth,
    int ParentIndex,
    bool IsAssigned);

/// <summary>フィルターの条件。空文字・null は「絞らない」。</summary>
public sealed record WorkItemListFilter(string? Text, string? State, string? WorkItemType)
{
    public bool HasFieldFilter => !string.IsNullOrEmpty(State) || !string.IsNullOrEmpty(WorkItemType);
}

/// <summary>
/// Work Item と PR を1つの一覧にまとめ、絞り込む（純ロジック）。
/// <para>PR は紐づく Work Item のすぐ下に並べ、どれにも紐づかない PR は末尾の見出しの下へ
/// （TaskAzure と同じ並べ方）。複数の Work Item に紐づく PR はそれぞれの下に出す。</para>
/// </summary>
public static class WorkItemList
{
    public const string UnlinkedPullRequestsTitle = "Work Item に紐づかない PR";

    public static IReadOnlyList<WorkItemListEntry> Build(
        IReadOnlyList<WorkItemTreeRow> rows, IReadOnlyList<AzureDevOpsPullRequest> pulls)
    {
        var shown = rows.Select(r => r.Item.Id).ToHashSet();
        var pullsByItem = new Dictionary<int, List<AzureDevOpsPullRequest>>();
        var unlinked = new List<AzureDevOpsPullRequest>();
        foreach (var pull in pulls)
        {
            var linked = pull.LinkedWorkItemIds.Where(shown.Contains).Distinct().ToList();
            if (linked.Count == 0)
                unlinked.Add(pull);
            foreach (var id in linked)
                (pullsByItem.TryGetValue(id, out var list) ? list : pullsByItem[id] = []).Add(pull);
        }

        var entries = new List<WorkItemListEntry>();
        var ancestors = new List<int>();   // 深さ → その深さで最後に置いた Work Item の位置
        foreach (var row in rows)
        {
            if (ancestors.Count > row.Depth)
                ancestors.RemoveRange(row.Depth, ancestors.Count - row.Depth);
            var parent = row.Depth > 0 && ancestors.Count >= row.Depth ? ancestors[row.Depth - 1] : -1;
            var index = entries.Count;
            entries.Add(new WorkItemListEntry(WorkItemListEntryKind.WorkItem, row.Item, null, "",
                row.Depth, parent, row.IsAssigned));
            ancestors.Add(index);
            if (pullsByItem.TryGetValue(row.Item.Id, out var itemPulls))
                foreach (var pull in itemPulls)
                    entries.Add(new WorkItemListEntry(WorkItemListEntryKind.PullRequest, null, pull, "",
                        row.Depth + 1, index, true));
        }
        if (unlinked.Count > 0)
        {
            var section = entries.Count;
            entries.Add(new WorkItemListEntry(WorkItemListEntryKind.Section, null, null, UnlinkedPullRequestsTitle,
                0, -1, true));
            foreach (var pull in unlinked)
                entries.Add(new WorkItemListEntry(WorkItemListEntryKind.PullRequest, null, pull, "", 1, section, true));
        }
        return entries;
    }

    /// <summary>
    /// 見せる行。規則：
    /// <list type="bullet">
    /// <item>Work Item は、文字（タイトル・状態・種類・ID・プロジェクト）と状態・種類の条件にすべて合えば残る。</item>
    /// <item>PR は、紐づく Work Item が条件に合って残るなら一緒に残る。状態・種類で絞っていないときは、
    /// PR 自身が文字に合っても残る（PR には状態・種類の欄が無い）。</item>
    /// <item>子が1つでも残れば、親（と見出し）は道しるべとして残す。</item>
    /// </list>
    /// </summary>
    public static bool[] Visible(IReadOnlyList<WorkItemListEntry> entries, WorkItemListFilter filter)
    {
        var visible = new bool[entries.Count];
        var selfMatch = new bool[entries.Count];
        var text = filter.Text?.Trim() ?? "";
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            switch (entry.Kind)
            {
                case WorkItemListEntryKind.WorkItem:
                    var item = entry.WorkItem!;
                    selfMatch[i] = Contains(text, item.Title, item.State, item.WorkItemType, item.Project, item.Id.ToString())
                        && Equal(filter.State, item.State) && Equal(filter.WorkItemType, item.WorkItemType);
                    visible[i] = selfMatch[i];
                    break;
                case WorkItemListEntryKind.PullRequest:
                    var pull = entry.PullRequest!;
                    var ownerMatched = entry.ParentIndex >= 0 && selfMatch[entry.ParentIndex];
                    visible[i] = ownerMatched
                        || (!filter.HasFieldFilter && Contains(text, pull.Title, pull.Repository, pull.Id.ToString()));
                    break;
            }
        }
        // 残った行の祖先を残す（親は必ず子より前にあるので、後ろから一度なめれば足りる）。
        for (var i = entries.Count - 1; i >= 0; i--)
            if (visible[i] && entries[i].ParentIndex >= 0)
                visible[entries[i].ParentIndex] = true;
        return visible;
    }

    private static bool Contains(string text, params string[] fields)
        => text.Length == 0 || fields.Any(f => f.Contains(text, StringComparison.CurrentCultureIgnoreCase));

    private static bool Equal(string? wanted, string actual)
        => string.IsNullOrEmpty(wanted) || string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase);
}
