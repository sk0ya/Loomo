using System.Windows.Input;

namespace sk0ya.Loomo.App.Services;

internal enum FilesColumnAddressAction
{
    None,
    Cancel,
    Navigate,
    MoveToSuggestions,
    AcceptSuggestion,
    ReturnToEditor,
}

/// <summary>ファイル一覧のアドレス入力で使うキー操作と閉じ方を決める。</summary>
internal static class FilesColumnAddressPolicy
{
    public static bool IsEditShortcut(Key key, ModifierKeys modifiers)
        => key == Key.L
            && (modifiers & ModifierKeys.Control) != 0
            && (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) == 0;

    public static FilesColumnAddressAction ResolveEditorKey(Key key, int suggestionCount)
        => key switch
        {
            Key.Escape => FilesColumnAddressAction.Cancel,
            Key.Enter => FilesColumnAddressAction.Navigate,
            Key.Down when suggestionCount > 0 => FilesColumnAddressAction.MoveToSuggestions,
            _ => FilesColumnAddressAction.None,
        };

    public static FilesColumnAddressAction ResolveSuggestionKey(Key key, int selectedIndex)
        => key switch
        {
            Key.Enter => FilesColumnAddressAction.AcceptSuggestion,
            Key.Escape => FilesColumnAddressAction.Cancel,
            Key.Up when selectedIndex <= 0 => FilesColumnAddressAction.ReturnToEditor,
            _ => FilesColumnAddressAction.None,
        };

    public static bool ShouldShowPopup(bool isEditing, int suggestionCount, bool hasError)
        => isEditing && (suggestionCount > 0 || hasError);

    public static bool ShouldDismiss(bool isEditing, bool focusWithinAddressUi)
        => isEditing && !focusWithinAddressUi;

    public static bool ShouldRestoreListFocus(bool hadAddressFocus, bool focusIsUnclaimed)
        => hadAddressFocus && focusIsUnclaimed;
}
