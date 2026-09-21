using System.Windows.Input;

namespace sk0ya.Loomo.App.Services;

internal enum BranchFilterKeyCommand
{
    None,
    Clear,
    MoveToList,
}

internal enum GitLogKeyCommand
{
    None,
    MoveDown,
    MoveUp,
    MoveTop,
    MoveBottom,
    OpenDiff,
    FocusFilter,
    ClearFilter,
}

internal enum GitLogFilterKeyCommand { None, Clear, FocusList }

internal readonly record struct GitLogKeyMapping(GitLogKeyCommand Action, bool PendingG);

/// <summary>Git ペインの入力キーを画面操作へ対応づける純粋ロジック。</summary>
internal static class GitSessionKeyboardMapper
{
    internal static GitLogFilterKeyCommand ResolveLogFilterKey(Key key, string text) => key switch
    {
        Key.Escape when text.Length > 0 => GitLogFilterKeyCommand.Clear,
        Key.Escape or Key.Down or Key.Enter => GitLogFilterKeyCommand.FocusList,
        _ => GitLogFilterKeyCommand.None,
    };

    internal static BranchFilterKeyCommand ResolveBranchFilterKey(Key key, string text, bool hasRows) => key switch
    {
        Key.Escape when text.Length > 0 => BranchFilterKeyCommand.Clear,
        Key.Escape => BranchFilterKeyCommand.MoveToList,
        Key.Down when hasRows => BranchFilterKeyCommand.MoveToList,
        _ => BranchFilterKeyCommand.None,
    };

    internal static GitLogKeyMapping ResolveLogKey(
        Key key, ModifierKeys modifiers, bool pendingG, bool hasFilters)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return new(GitLogKeyCommand.None, false);

        var shift = (modifiers & ModifierKeys.Shift) != 0;
        return key switch
        {
            Key.G when shift => new(GitLogKeyCommand.MoveBottom, false),
            Key.G when pendingG => new(GitLogKeyCommand.MoveTop, false),
            Key.G => new(GitLogKeyCommand.None, PendingG: true),
            Key.J when !shift => new(GitLogKeyCommand.MoveDown, false),
            Key.K when !shift => new(GitLogKeyCommand.MoveUp, false),
            Key.Enter => new(GitLogKeyCommand.OpenDiff, false),
            Key.OemQuestion or Key.Divide when !shift => new(GitLogKeyCommand.FocusFilter, false),
            Key.Escape when hasFilters => new(GitLogKeyCommand.ClearFilter, false),
            _ => new(GitLogKeyCommand.None, false),
        };
    }
}
