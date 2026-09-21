using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

internal enum WorkspaceTabCloseScope { Selected, Others, All }

internal sealed record WorkspaceTabClosePlan(TabEntryKind Kind, IReadOnlyList<Guid> TabIds);

internal static class WorkspaceTabCloseCoordinator
{
    public static async Task<bool> ExecuteOneAsync(
        TabEntryKind? kind,
        Guid id,
        Func<Guid, Task> closeTerminal,
        Action<Guid> closeEditor,
        Func<Guid, Task> closeBrowser)
    {
        if (kind is not { } tabKind)
            return false;
        await ExecuteAsync(new WorkspaceTabClosePlan(tabKind, new[] { id }),
            closeTerminal, closeEditor, closeBrowser);
        return true;
    }

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

/// <summary>サイドバーのタブ種別と「選択／他／すべて」から、閉じる ID のスナップショットを作る。</summary>
internal static class WorkspaceTabClosePolicy
{
    public static TabEntryKind? ResolveKind(
        Guid tabId,
        IEnumerable<Guid> terminalTabIds,
        IEnumerable<Guid> editorTabIds,
        IEnumerable<Guid> browserTabIds)
        => terminalTabIds.Contains(tabId) ? TabEntryKind.Terminal
            : editorTabIds.Contains(tabId) ? TabEntryKind.Editor
            : browserTabIds.Contains(tabId) ? TabEntryKind.Browser
            : null;

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
            _ => Array.Empty<Guid>(),
        };
        return new WorkspaceTabClosePlan(kind, closeIds.ToArray());
    }
}
