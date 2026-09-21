using System.Windows.Input;

namespace sk0ya.Loomo.App.Services;

internal enum FilesColumnKeyAction
{
    None,
    GoBack,
    GoForward,
    ShowProperties,
    Copy,
    Cut,
    Paste,
    Duplicate,
    Undo,
    Redo,
    OpenFilter,
    CloseFilter,
    OpenSelected,
    GoUp,
    Rename,
    Delete,
    Refresh,
    MoveNext,
    MovePrevious,
}

internal readonly record struct FilesColumnKeyResolution(
    FilesColumnKeyAction Action, bool MarkHandled);

/// <summary>ファイル一覧のキーと操作の対応を決める。選択・フォーカス変更は View に任せる。</summary>
internal static class FilesColumnKeyboardPolicy
{
    public static FilesColumnKeyResolution Resolve(
        Key key,
        Key systemKey,
        ModifierKeys modifiers,
        bool filterOpen,
        Func<bool> canGoBack,
        Func<bool> canGoForward)
    {
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            var effectiveKey = key == Key.System ? systemKey : key;
            var action = effectiveKey switch
            {
                Key.Left when canGoBack() => FilesColumnKeyAction.GoBack,
                Key.Right when canGoForward() => FilesColumnKeyAction.GoForward,
                Key.Enter or Key.Return => FilesColumnKeyAction.ShowProperties,
                _ => FilesColumnKeyAction.None,
            };
            return new(action, MarkHandled: action != FilesColumnKeyAction.None);
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            var action = key switch
            {
                Key.C => FilesColumnKeyAction.Copy,
                Key.X => FilesColumnKeyAction.Cut,
                Key.V => FilesColumnKeyAction.Paste,
                Key.D => FilesColumnKeyAction.Duplicate,
                Key.Z when (modifiers & ModifierKeys.Shift) != 0 => FilesColumnKeyAction.Redo,
                Key.Z => FilesColumnKeyAction.Undo,
                Key.Y => FilesColumnKeyAction.Redo,
                _ => FilesColumnKeyAction.None,
            };
            return new(action, MarkHandled: action != FilesColumnKeyAction.None);
        }

        var ordinaryAction = key switch
        {
            Key.OemQuestion or Key.Divide => FilesColumnKeyAction.OpenFilter,
            Key.Escape when filterOpen => FilesColumnKeyAction.CloseFilter,
            Key.Enter => FilesColumnKeyAction.OpenSelected,
            Key.Back => FilesColumnKeyAction.GoUp,
            Key.F2 => FilesColumnKeyAction.Rename,
            Key.Delete => FilesColumnKeyAction.Delete,
            Key.F5 => FilesColumnKeyAction.Refresh,
            Key.J => FilesColumnKeyAction.MoveNext,
            Key.K => FilesColumnKeyAction.MovePrevious,
            _ => FilesColumnKeyAction.None,
        };
        return new(ordinaryAction, MarkHandled: ordinaryAction != FilesColumnKeyAction.None);
    }
}
