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
    Reload,
}

internal enum GitLogFilterKeyCommand { None, Clear, FocusList, Close }

internal readonly record struct GitLogKeyMapping(GitLogKeyCommand Action, bool PendingG);

/// <summary>Git ペインの入力キーを画面操作へ対応づける純粋ロジック。</summary>
internal static class GitSessionKeyboardMapper
{
    internal static GitLogFilterKeyCommand ResolveLogFilterKey(Key key, string text) => key switch
    {
        Key.Escape when text.Length > 0 => GitLogFilterKeyCommand.Clear,
        // 空の欄で Esc＝帯を畳んで一覧へ戻る（帯はふだん畳んでおく・効いている条件があれば畳まれない）。
        Key.Escape => GitLogFilterKeyCommand.Close,
        Key.Down or Key.Enter => GitLogFilterKeyCommand.FocusList,
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
            // 手で読み直す（監視の取りこぼしの逃げ道）。常設の ⟳ ボタンは一覧の高さを取るので外した。
            Key.F5 => new(GitLogKeyCommand.Reload, false),
            _ => new(GitLogKeyCommand.None, false),
        };
    }
}
