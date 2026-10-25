using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>閉じる範囲。<see cref="After"/>／<see cref="Before"/> はタブの並び（番号の並び・§24.47）で、選んだタブより後ろ／前（§24.89）。</summary>
internal enum WorkspaceTabCloseScope { Selected, Others, All, After, Before }

internal sealed record WorkspaceTabClosePlan(TabEntryKind Kind, IReadOnlyList<Guid> TabIds);

internal static class WorkspaceTabCloseCoordinator
{
    public static async Task ExecuteAsync(
        WorkspaceTabClosePlan plan,
        Func<Guid, Task> closeTerminal,
        Action<Guid> closeEditor,
        Func<Guid, Task> closeBrowser)
    {
        foreach (var id in plan.TabIds)
        {
            switch (plan.Kind)
            {
                case TabEntryKind.Terminal:
                    await closeTerminal(id);
                    break;
                case TabEntryKind.Editor:
                    closeEditor(id);
                    break;
                case TabEntryKind.Browser:
                    await closeBrowser(id);
                    break;
            }
        }
    }
}

/// <summary>サイドバーのタブ種別と「選択／他／すべて／後ろ／前」から、閉じる ID のスナップショットを作る。</summary>
internal static class WorkspaceTabClosePolicy
{
    public static WorkspaceTabClosePlan CreatePlan(
        TabEntryKind kind,
        Guid selectedId,
        WorkspaceTabCloseScope scope,
        IEnumerable<Guid> terminalTabIds,
        IEnumerable<Guid> editorTabIds,
        IEnumerable<Guid> browserTabIds)
    {
        IEnumerable<Guid> tabIds = kind switch
        {
            TabEntryKind.Terminal => terminalTabIds,
            TabEntryKind.Editor => editorTabIds,
            TabEntryKind.Browser => browserTabIds,
            _ => Array.Empty<Guid>(),
        };

        IEnumerable<Guid> closeIds = scope switch
        {
            WorkspaceTabCloseScope.Selected => new[] { selectedId },
            WorkspaceTabCloseScope.Others => tabIds.Where(id => id != selectedId),
            WorkspaceTabCloseScope.All => tabIds,
            WorkspaceTabCloseScope.After => tabIds.SkipWhile(id => id != selectedId).Skip(1),
            // 選んだタブが並びに無いなら何も閉じない（前を全部閉じてしまわないように）。
            WorkspaceTabCloseScope.Before => tabIds.Contains(selectedId) ? tabIds.TakeWhile(id => id != selectedId) : Array.Empty<Guid>(),
            _ => Array.Empty<Guid>(),
        };
        return new WorkspaceTabClosePlan(kind, closeIds.ToArray());
    }
}
