using System.Windows.Input;

namespace sk0ya.Loomo.App.Services;

internal enum BrowserKeyboardAction
{
    Reload,
    NavigateBack,
    NavigateForward,
    FocusAddress,
    OpenFind,
    FindNext,
    FindPrevious,
    ToggleBookmark,
    ToggleBookmarkBar,
    ZoomIn,
    ZoomOut,
    ZoomReset,
    CloseFind,
}

internal enum BrowserAddressKeyAction
{
    Commit,
    MoveDown,
    MoveUp,
    DismissSuggestions,
    RestoreCurrentAddress,
}

internal enum BrowserFindKeyAction
{
    Next,
    Previous,
    Close,
}

/// <summary>ブラウザ chrome のキー入力を、View が実行する意図へ変換する。</summary>
internal static class BrowserChromePolicy
{
    public static BrowserKeyboardAction? ResolveKeyboardAction(
        Key key, Key systemKey, ModifierKeys modifiers, bool findOpen)
    {
        if (key == Key.System)
            key = systemKey;

        var control = modifiers.HasFlag(ModifierKeys.Control);
        var alt = modifiers.HasFlag(ModifierKeys.Alt);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        // Ctrl+Shift+F はアプリ検索へ渡し、Ctrl+T/W もShell側のキー操作へ渡す。
        // ズームだけは Shift 付きの「+」入力を許す。
        return key switch
        {
            Key.F5 => BrowserKeyboardAction.Reload,
            Key.R when control && !shift => BrowserKeyboardAction.Reload,
            Key.Left when alt => BrowserKeyboardAction.NavigateBack,
            Key.Right when alt => BrowserKeyboardAction.NavigateForward,
            Key.L when control && !shift => BrowserKeyboardAction.FocusAddress,
            Key.F when control && !shift => BrowserKeyboardAction.OpenFind,
            Key.G when control => shift ? BrowserKeyboardAction.FindPrevious : BrowserKeyboardAction.FindNext,
            Key.D when control && !shift => BrowserKeyboardAction.ToggleBookmark,
            Key.B when control && shift => BrowserKeyboardAction.ToggleBookmarkBar,
            Key.OemPlus or Key.Add when control => BrowserKeyboardAction.ZoomIn,
            Key.OemMinus or Key.Subtract when control => BrowserKeyboardAction.ZoomOut,
            Key.D0 or Key.NumPad0 when control => BrowserKeyboardAction.ZoomReset,
            Key.Escape when findOpen => BrowserKeyboardAction.CloseFind,
            _ => null,
        };
    }

    public static BrowserAddressKeyAction? ResolveAddressAction(Key key, bool suggestionsOpen)
        => key switch
        {
            Key.Enter => BrowserAddressKeyAction.Commit,
            Key.Down when suggestionsOpen => BrowserAddressKeyAction.MoveDown,
            Key.Up when suggestionsOpen => BrowserAddressKeyAction.MoveUp,
            Key.Escape when suggestionsOpen => BrowserAddressKeyAction.DismissSuggestions,
            Key.Escape => BrowserAddressKeyAction.RestoreCurrentAddress,
            _ => null,
        };

    public static BrowserFindKeyAction? ResolveFindAction(Key key, ModifierKeys modifiers)
        => key switch
        {
            Key.Enter => modifiers.HasFlag(ModifierKeys.Shift)
                ? BrowserFindKeyAction.Previous
                : BrowserFindKeyAction.Next,
            Key.Escape => BrowserFindKeyAction.Close,
            _ => null,
        };

    public static int NextSuggestionIndex(int selectedIndex, int itemCount, int delta)
        => itemCount <= 0 ? -1 : Math.Clamp(selectedIndex + delta, 0, itemCount - 1);

    public static string AddressToCommit(
        bool suggestionsOpen, string? selectedSuggestionUrl, string typedAddress)
        => suggestionsOpen ? selectedSuggestionUrl ?? typedAddress : typedAddress;
}
