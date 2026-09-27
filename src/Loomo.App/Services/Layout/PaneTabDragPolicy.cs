using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>切り離したタブをメインのペインへ戻すときの規則。</summary>
internal static class PaneTabDragPolicy
{
    internal static bool CanReturnToPane(string paneTag, TabEntryKind returnKind)
        => TabKindForPane(paneTag) == returnKind;

    private static TabEntryKind? TabKindForPane(string paneTag) => paneTag switch
    {
        "Editor" => TabEntryKind.Editor,
        "Terminal" => TabEntryKind.Terminal,
        "Browser" => TabEntryKind.Browser,
        _ => null,
    };
}
